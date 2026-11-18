using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Windows.Storage.Streams;

namespace scarpa_connection_manager_win.Services
{
    public class LocalWebServer
    {
        private readonly HttpListener _listener;
        private readonly string _baseFolder;
        private readonly int _port;

        public string BaseUrl => $"http://127.0.0.1:{_port}/";

        public LocalWebServer(string baseFolder)
        {
            _baseFolder = baseFolder;
            _port = GetAvailablePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add(BaseUrl);
        }

        public void Start()
        {
            _listener.Start();
            Task.Run(() => HandleRequestsAsync());
        }

        public void Stop()
        {
            if (_listener.IsListening)
            {
                _listener.Stop();
                _listener.Close();
            }
        }

        private async Task HandleRequestsAsync()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    ProcessRequest(context);
                }
                catch (HttpListenerException) { /* Ignored on shutdown */ }
                catch (ObjectDisposedException) { /* Ignored on close */ }
            }
        }

        private void ProcessRequest(HttpListenerContext context)
        {
            try
            {
                string filename = context?.Request?.Url?.AbsolutePath?.TrimStart('/') ?? string.Empty;


                if (string.IsNullOrEmpty(filename)) filename = "UserGuide.html";

                // Sanitize path to prevent directory traversal attacks
                filename = filename.Replace("..", "");
                string filePath = Path.Combine(_baseFolder, filename);

                if (File.Exists(filePath))
                {
                    byte[] buffer = File.ReadAllBytes(filePath);
                    if (context?.Response != null)
                    {
                        context.Response.ContentLength64 = buffer != null ? buffer.Length : 0;
                    }


                    string ext = Path.GetExtension(filePath).ToLower();
                    context.Response.ContentType = ext switch
                    {
                        ".html" => "text/html",
                        ".css" => "text/css",
                        ".js" => "application/javascript",
                        ".png" => "image/png",
                        ".jpg" => "image/jpeg",
                        ".svg" => "image/svg+xml",
                        _ => "application/octet-stream"
                    };

                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.StatusCode = 200;
                }
                else
                {
                    if (context?.Response != null)
                    {
                        context.Response.StatusCode = 404;
                    }

                }
            }
            catch
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.OutputStream.Close();
            }
        }

        // Finds a guaranteed unused local port to prevent binding crashes
        private int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}