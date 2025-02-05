using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Net;
using Emby.Server.Implementations.Services;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Extensions;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ServiceStack.Text.Jsv;

namespace Emby.Server.Implementations.HttpServer
{
    public class HttpListenerHost : IHttpServer, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IServerConfigurationManager _config;
        private readonly INetworkManager _networkManager;
        private readonly IServerApplicationHost _appHost;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IXmlSerializer _xmlSerializer;
        private readonly IHttpListener _socketListener;
        private readonly Func<Type, Func<string, object>> _funcParseFn;
        private readonly string _defaultRedirectPath;
        private readonly Dictionary<Type, Type> ServiceOperationsMap = new Dictionary<Type, Type>();
        private IWebSocketListener[] _webSocketListeners = Array.Empty<IWebSocketListener>();
        private readonly List<IWebSocketConnection> _webSocketConnections = new List<IWebSocketConnection>();
        private bool _disposed = false;

        public HttpListenerHost(
            IServerApplicationHost applicationHost,
            ILogger<HttpListenerHost> logger,
            IServerConfigurationManager config,
            IConfiguration configuration,
            INetworkManager networkManager,
            IJsonSerializer jsonSerializer,
            IXmlSerializer xmlSerializer,
            IHttpListener socketListener)
        {
            _appHost = applicationHost;
            _logger = logger;
            _config = config;
            _defaultRedirectPath = configuration["HttpListenerHost:DefaultRedirectPath"];
            _networkManager = networkManager;
            _jsonSerializer = jsonSerializer;
            _xmlSerializer = xmlSerializer;
            _socketListener = socketListener;
            _socketListener.WebSocketConnected = OnWebSocketConnected;

            _funcParseFn = t => s => JsvReader.GetParseFn(t)(s);

            Instance = this;
            ResponseFilters = Array.Empty<Action<IRequest, HttpResponse, object>>();
        }

        public Action<IRequest, HttpResponse, object>[] ResponseFilters { get; set; }

        public static HttpListenerHost Instance { get; protected set; }

        public string[] UrlPrefixes { get; private set; }

        public string GlobalResponse { get; set; }

        public ServiceController ServiceController { get; private set; }

        public event EventHandler<GenericEventArgs<IWebSocketConnection>> WebSocketConnected;

        public object CreateInstance(Type type)
        {
            return _appHost.CreateInstance(type);
        }

        /// <summary>
        /// Applies the request filters. Returns whether or not the request has been handled
        /// and no more processing should be done.
        /// </summary>
        /// <returns></returns>
        public void ApplyRequestFilters(IRequest req, HttpResponse res, object requestDto)
        {
            //Exec all RequestFilter attributes with Priority < 0
            var attributes = GetRequestFilterAttributes(requestDto.GetType());

            int count = attributes.Count;
            int i = 0;
            for (; i < count && attributes[i].Priority < 0; i++)
            {
                var attribute = attributes[i];
                attribute.RequestFilter(req, res, requestDto);
            }

            //Exec remaining RequestFilter attributes with Priority >= 0
            for (; i < count && attributes[i].Priority >= 0; i++)
            {
                var attribute = attributes[i];
                attribute.RequestFilter(req, res, requestDto);
            }
        }

        public Type GetServiceTypeByRequest(Type requestType)
        {
            ServiceOperationsMap.TryGetValue(requestType, out var serviceType);
            return serviceType;
        }

        public void AddServiceInfo(Type serviceType, Type requestType)
        {
            ServiceOperationsMap[requestType] = serviceType;
        }

        private List<IHasRequestFilter> GetRequestFilterAttributes(Type requestDtoType)
        {
            var attributes = requestDtoType.GetCustomAttributes(true).OfType<IHasRequestFilter>().ToList();

            var serviceType = GetServiceTypeByRequest(requestDtoType);
            if (serviceType != null)
            {
                attributes.AddRange(serviceType.GetCustomAttributes(true).OfType<IHasRequestFilter>());
            }

            attributes.Sort((x, y) => x.Priority - y.Priority);

            return attributes;
        }

        private void OnWebSocketConnected(WebSocketConnectEventArgs e)
        {
            if (_disposed)
            {
                return;
            }

            var connection = new WebSocketConnection(e.WebSocket, e.Endpoint, _jsonSerializer, _logger)
            {
                OnReceive = ProcessWebSocketMessageReceived,
                Url = e.Url,
                QueryString = e.QueryString ?? new QueryCollection()
            };

            connection.Closed += OnConnectionClosed;

            lock (_webSocketConnections)
            {
                _webSocketConnections.Add(connection);
            }

            WebSocketConnected?.Invoke(this, new GenericEventArgs<IWebSocketConnection>(connection));
        }

        private void OnConnectionClosed(object sender, EventArgs e)
        {
            lock (_webSocketConnections)
            {
                _webSocketConnections.Remove((IWebSocketConnection)sender);
            }
        }

        private static Exception GetActualException(Exception ex)
        {
            if (ex is AggregateException agg)
            {
                var inner = agg.InnerException;
                if (inner != null)
                {
                    return GetActualException(inner);
                }
                else
                {
                    var inners = agg.InnerExceptions;
                    if (inners != null && inners.Count > 0)
                    {
                        return GetActualException(inners[0]);
                    }
                }
            }

            return ex;
        }

        private int GetStatusCode(Exception ex)
        {
            switch (ex)
            {
                case ArgumentException _: return 400;
                case SecurityException _: return 401;
                case DirectoryNotFoundException _:
                case FileNotFoundException _:
                case ResourceNotFoundException _: return 404;
                case MethodNotAllowedException _: return 405;
                case RemoteServiceUnavailableException _: return 502;
                default: return 500;
            }
        }

        private async Task ErrorHandler(Exception ex, IRequest httpReq, bool logExceptionStackTrace, bool logExceptionMessage)
        {
            try
            {
                ex = GetActualException(ex);

                if (logExceptionStackTrace)
                {
                    _logger.LogError(ex, "Error processing request");
                }
                else if (logExceptionMessage)
                {
                    _logger.LogError(ex.Message);
                }

                var httpRes = httpReq.Response;

                if (httpRes.HasStarted)
                {
                    return;
                }

                var statusCode = GetStatusCode(ex);
                httpRes.StatusCode = statusCode;

                httpRes.ContentType = "text/html";
                await httpRes.WriteAsync(NormalizeExceptionMessage(ex.Message)).ConfigureAwait(false);
            }
            catch (Exception errorEx)
            {
                _logger.LogError(errorEx, "Error this.ProcessRequest(context)(Exception while writing error to the response)");
            }
        }

        private string NormalizeExceptionMessage(string msg)
        {
            if (msg == null)
            {
                return string.Empty;
            }

            // Strip any information we don't want to reveal

            msg = msg.Replace(_config.ApplicationPaths.ProgramSystemPath, string.Empty, StringComparison.OrdinalIgnoreCase);
            msg = msg.Replace(_config.ApplicationPaths.ProgramDataPath, string.Empty, StringComparison.OrdinalIgnoreCase);

            return msg;
        }

        /// <summary>
        /// Shut down the Web Service
        /// </summary>
        public void Stop()
        {
            List<IWebSocketConnection> connections;

            lock (_webSocketConnections)
            {
                connections = _webSocketConnections.ToList();
                _webSocketConnections.Clear();
            }

            foreach (var connection in connections)
            {
                try
                {
                    connection.Dispose();
                }
                catch
                {

                }
            }
        }

        public static string RemoveQueryStringByKey(string url, string key)
        {
            var uri = new Uri(url);

            // this gets all the query string key value pairs as a collection
            var newQueryString = QueryHelpers.ParseQuery(uri.Query);

            var originalCount = newQueryString.Count;

            if (originalCount == 0)
            {
                return url;
            }

            // this removes the key if exists
            newQueryString.Remove(key);

            if (originalCount == newQueryString.Count)
            {
                return url;
            }

            // this gets the page path from root without QueryString
            string pagePathWithoutQueryString = url.Split(new[] { '?' }, StringSplitOptions.RemoveEmptyEntries)[0];

            return newQueryString.Count > 0
                ? QueryHelpers.AddQueryString(pagePathWithoutQueryString, newQueryString.ToDictionary(kv => kv.Key, kv => kv.Value.ToString()))
                : pagePathWithoutQueryString;
        }

        private static string GetUrlToLog(string url)
        {
            url = RemoveQueryStringByKey(url, "api_key");

            return url;
        }

        private static string NormalizeConfiguredLocalAddress(string address)
        {
            var add = address.AsSpan().Trim('/');
            int index = add.IndexOf('/');
            if (index != -1)
            {
                add = add.Slice(index + 1);
            }

            return add.TrimStart('/').ToString();
        }

        private bool ValidateHost(string host)
        {
            var hosts = _config
                .Configuration
                .LocalNetworkAddresses
                .Select(NormalizeConfiguredLocalAddress)
                .ToList();

            if (hosts.Count == 0)
            {
                return true;
            }

            host = host ?? string.Empty;

            if (_networkManager.IsInPrivateAddressSpace(host))
            {
                hosts.Add("localhost");
                hosts.Add("127.0.0.1");

                return hosts.Any(i => host.IndexOf(i, StringComparison.OrdinalIgnoreCase) != -1);
            }

            return true;
        }

        private bool ValidateRequest(string remoteIp, bool isLocal)
        {
            if (isLocal)
            {
                return true;
            }

            if (_config.Configuration.EnableRemoteAccess)
            {
                var addressFilter = _config.Configuration.RemoteIPFilter.Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();

                if (addressFilter.Length > 0 && !_networkManager.IsInLocalNetwork(remoteIp))
                {
                    if (_config.Configuration.IsRemoteIPFilterBlacklist)
                    {
                        return !_networkManager.IsAddressInSubnets(remoteIp, addressFilter);
                    }
                    else
                    {
                        return _networkManager.IsAddressInSubnets(remoteIp, addressFilter);
                    }
                }
            }
            else
            {
                if (!_networkManager.IsInLocalNetwork(remoteIp))
                {
                    return false;
                }
            }

            return true;
        }

        private bool ValidateSsl(string remoteIp, string urlString)
        {
            if (_config.Configuration.RequireHttps && _appHost.EnableHttps && !_config.Configuration.IsBehindProxy)
            {
                if (urlString.IndexOf("https://", StringComparison.OrdinalIgnoreCase) == -1)
                {
                    // These are hacks, but if these ever occur on ipv6 in the local network they could be incorrectly redirected
                    if (urlString.IndexOf("system/ping", StringComparison.OrdinalIgnoreCase) != -1
                        || urlString.IndexOf("dlna/", StringComparison.OrdinalIgnoreCase) != -1)
                    {
                        return true;
                    }

                    if (!_networkManager.IsInLocalNetwork(remoteIp))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Overridable method that can be used to implement a custom hnandler
        /// </summary>
        public async Task RequestHandler(IHttpRequest httpReq, string urlString, string host, string localPath, CancellationToken cancellationToken)
        {
            var stopWatch = new Stopwatch();
            stopWatch.Start();
            var httpRes = httpReq.Response;
            string urlToLog = null;
            string remoteIp = httpReq.RemoteIp;

            try
            {
                if (_disposed)
                {
                    httpRes.StatusCode = 503;
                    httpRes.ContentType = "text/plain";
                    await httpRes.WriteAsync("Server shutting down", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!ValidateHost(host))
                {
                    httpRes.StatusCode = 400;
                    httpRes.ContentType = "text/plain";
                    await httpRes.WriteAsync("Invalid host", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!ValidateRequest(remoteIp, httpReq.IsLocal))
                {
                    httpRes.StatusCode = 403;
                    httpRes.ContentType = "text/plain";
                    await httpRes.WriteAsync("Forbidden", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!ValidateSsl(httpReq.RemoteIp, urlString))
                {
                    RedirectToSecureUrl(httpReq, httpRes, urlString);
                    return;
                }

                if (string.Equals(httpReq.Verb, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    httpRes.StatusCode = 200;
                    httpRes.Headers.Add("Access-Control-Allow-Origin", "*");
                    httpRes.Headers.Add("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, PATCH, OPTIONS");
                    httpRes.Headers.Add("Access-Control-Allow-Headers", "Content-Type, Authorization, Range, X-MediaBrowser-Token, X-Emby-Authorization");
                    httpRes.ContentType = "text/plain";
                    await httpRes.WriteAsync(string.Empty, cancellationToken).ConfigureAwait(false);
                    return;
                }

                urlToLog = GetUrlToLog(urlString);

                if (string.Equals(localPath, "/emby/", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(localPath, "/mediabrowser/", StringComparison.OrdinalIgnoreCase))
                {
                    httpRes.Redirect(_defaultRedirectPath);
                    return;
                }

                if (string.Equals(localPath, "/emby", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(localPath, "/mediabrowser", StringComparison.OrdinalIgnoreCase))
                {
                    httpRes.Redirect("emby/" + _defaultRedirectPath);
                    return;
                }

                if (localPath.IndexOf("mediabrowser/web", StringComparison.OrdinalIgnoreCase) != -1)
                {
                    httpRes.StatusCode = 200;
                    httpRes.ContentType = "text/html";
                    var newUrl = urlString.Replace("mediabrowser", "emby", StringComparison.OrdinalIgnoreCase)
                        .Replace("/dashboard/", "/web/", StringComparison.OrdinalIgnoreCase);

                    if (!string.Equals(newUrl, urlString, StringComparison.OrdinalIgnoreCase))
                    {
                        await httpRes.WriteAsync(
                            "<!doctype html><html><head><title>Emby</title></head><body>Please update your Emby bookmark to <a href=\"" +
                            newUrl + "\">" + newUrl + "</a></body></html>",
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                if (localPath.IndexOf("dashboard/", StringComparison.OrdinalIgnoreCase) != -1 &&
                    localPath.IndexOf("web/dashboard", StringComparison.OrdinalIgnoreCase) == -1)
                {
                    httpRes.StatusCode = 200;
                    httpRes.ContentType = "text/html";
                    var newUrl = urlString.Replace("mediabrowser", "emby", StringComparison.OrdinalIgnoreCase)
                        .Replace("/dashboard/", "/web/", StringComparison.OrdinalIgnoreCase);

                    if (!string.Equals(newUrl, urlString, StringComparison.OrdinalIgnoreCase))
                    {
                        await httpRes.WriteAsync(
                            "<!doctype html><html><head><title>Emby</title></head><body>Please update your Emby bookmark to <a href=\"" +
                            newUrl + "\">" + newUrl + "</a></body></html>",
                            cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                if (string.Equals(localPath, "/web", StringComparison.OrdinalIgnoreCase))
                {
                    httpRes.Redirect(_defaultRedirectPath);
                    return;
                }

                if (string.Equals(localPath, "/web/", StringComparison.OrdinalIgnoreCase))
                {
                    httpRes.Redirect("../" + _defaultRedirectPath);
                    return;
                }

                if (string.Equals(localPath, "/", StringComparison.OrdinalIgnoreCase))
                {
                    httpRes.Redirect(_defaultRedirectPath);
                    return;
                }

                if (string.IsNullOrEmpty(localPath))
                {
                    httpRes.Redirect("/" + _defaultRedirectPath);
                    return;
                }

                if (!string.Equals(httpReq.QueryString["r"], "0", StringComparison.OrdinalIgnoreCase))
                {
                    if (localPath.EndsWith("web/dashboard.html", StringComparison.OrdinalIgnoreCase))
                    {
                        httpRes.Redirect("index.html#!/dashboard.html");
                    }

                    if (localPath.EndsWith("web/home.html", StringComparison.OrdinalIgnoreCase))
                    {
                        httpRes.Redirect("index.html");
                    }
                }

                if (!string.IsNullOrEmpty(GlobalResponse))
                {
                    // We don't want the address pings in ApplicationHost to fail
                    if (localPath.IndexOf("system/ping", StringComparison.OrdinalIgnoreCase) == -1)
                    {
                        httpRes.StatusCode = 503;
                        httpRes.ContentType = "text/html";
                        await httpRes.WriteAsync(GlobalResponse, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }

                var handler = GetServiceHandler(httpReq);

                if (handler != null)
                {
                    await handler.ProcessRequestAsync(this, httpReq, httpRes, _logger, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await ErrorHandler(new FileNotFoundException(), httpReq, false, false).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException || ex is IOException || ex is OperationCanceledException)
            {
                await ErrorHandler(ex, httpReq, false, false).ConfigureAwait(false);
            }
            catch (SecurityException ex)
            {
                await ErrorHandler(ex, httpReq, false, true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var logException = !string.Equals(ex.GetType().Name, "SocketException", StringComparison.OrdinalIgnoreCase);

                await ErrorHandler(ex, httpReq, logException, false).ConfigureAwait(false);
            }
            finally
            {
                stopWatch.Stop();
                var elapsed = stopWatch.Elapsed;
                if (elapsed.TotalMilliseconds > 500)
                {
                    _logger.LogWarning("HTTP Response {StatusCode} to {RemoteIp}. Time (slow): {Elapsed:g}. {Url}", httpRes.StatusCode, remoteIp, elapsed, urlToLog);
                }
            }
        }

        // Entry point for HttpListener
        public ServiceHandler GetServiceHandler(IHttpRequest httpReq)
        {
            var pathInfo = httpReq.PathInfo;

            pathInfo = ServiceHandler.GetSanitizedPathInfo(pathInfo, out string contentType);
            var restPath = ServiceController.GetRestPathForRequest(httpReq.HttpMethod, pathInfo);
            if (restPath != null)
            {
                return new ServiceHandler(restPath, contentType);
            }

            _logger.LogError("Could not find handler for {PathInfo}", pathInfo);
            return null;
        }

        private void RedirectToSecureUrl(IHttpRequest httpReq, HttpResponse httpRes, string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
            {
                var builder = new UriBuilder(uri)
                {
                    Port = _config.Configuration.PublicHttpsPort,
                    Scheme = "https"
                };
                url = builder.Uri.ToString();
            }

            httpRes.Redirect(url);
        }

        /// <summary>
        /// Adds the rest handlers.
        /// </summary>
        /// <param name="services">The services.</param>
        /// <param name="listeners"></param>
        /// <param name="urlPrefixes"></param>
        public void Init(IEnumerable<IService> services, IEnumerable<IWebSocketListener> listeners, IEnumerable<string> urlPrefixes)
        {
            _webSocketListeners = listeners.ToArray();
            UrlPrefixes = urlPrefixes.ToArray();
            ServiceController = new ServiceController();

            var types = services.Select(r => r.GetType());
            ServiceController.Init(this, types);

            ResponseFilters = new Action<IRequest, HttpResponse, object>[]
            {
                new ResponseFilter(_logger).FilterResponse
            };
        }

        public RouteAttribute[] GetRouteAttributes(Type requestType)
        {
            var routes = requestType.GetTypeInfo().GetCustomAttributes<RouteAttribute>(true).ToList();
            var clone = routes.ToList();

            foreach (var route in clone)
            {
                routes.Add(new RouteAttribute(NormalizeEmbyRoutePath(route.Path), route.Verbs)
                {
                    Notes = route.Notes,
                    Priority = route.Priority,
                    Summary = route.Summary
                });

                routes.Add(new RouteAttribute(NormalizeMediaBrowserRoutePath(route.Path), route.Verbs)
                {
                    Notes = route.Notes,
                    Priority = route.Priority,
                    Summary = route.Summary
                });

                // needed because apps add /emby, and some users also add /emby, thereby double prefixing
                routes.Add(new RouteAttribute(DoubleNormalizeEmbyRoutePath(route.Path), route.Verbs)
                {
                    Notes = route.Notes,
                    Priority = route.Priority,
                    Summary = route.Summary
                });
            }

            return routes.ToArray();
        }

        public Func<string, object> GetParseFn(Type propertyType)
        {
            return _funcParseFn(propertyType);
        }

        public void SerializeToJson(object o, Stream stream)
        {
            _jsonSerializer.SerializeToStream(o, stream);
        }

        public void SerializeToXml(object o, Stream stream)
        {
            _xmlSerializer.SerializeToStream(o, stream);
        }

        public Task<object> DeserializeXml(Type type, Stream stream)
        {
            return Task.FromResult(_xmlSerializer.DeserializeFromStream(type, stream));
        }

        public Task<object> DeserializeJson(Type type, Stream stream)
        {
            return _jsonSerializer.DeserializeFromStreamAsync(stream, type);
        }

        public Task ProcessWebSocketRequest(HttpContext context)
        {
            return _socketListener.ProcessWebSocketRequest(context);
        }

        //TODO Add Jellyfin Route Path Normalizer
        private static string NormalizeEmbyRoutePath(string path)
        {
            if (path.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                return "/emby" + path;
            }

            return "emby/" + path;
        }

        private static string NormalizeMediaBrowserRoutePath(string path)
        {
            if (path.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                return "/mediabrowser" + path;
            }

            return "mediabrowser/" + path;
        }

        private static string DoubleNormalizeEmbyRoutePath(string path)
        {
            if (path.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                return "/emby/emby" + path;
            }

            return "emby/emby/" + path;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                Stop();
            }

            _disposed = true;
        }

        /// <summary>
        /// Processes the web socket message received.
        /// </summary>
        /// <param name="result">The result.</param>
        private Task ProcessWebSocketMessageReceived(WebSocketMessageInfo result)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _logger.LogDebug("Websocket message received: {0}", result.MessageType);

            IEnumerable<Task> GetTasks()
            {
                foreach (var x in _webSocketListeners)
                {
                    yield return x.ProcessMessageAsync(result);
                }
            }

            return Task.WhenAll(GetTasks());
        }
    }
}
