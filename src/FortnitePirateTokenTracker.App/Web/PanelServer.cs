using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FortnitePirateTokenTracker.App.Web;

/// <summary>
/// Servidor HTTP local sin dependencias externas. Se apoya en TcpListener para
/// evitar reservas de URL de HttpListener en Windows.
/// </summary>
public sealed class PanelServer
{
    private readonly TcpListener _listener;
    private readonly string _webRoot;
    private readonly Func<string, string, string?, (int Status, string ContentType, byte[] Body)> _apiHandler;

    public PanelServer(int port, string webRoot,
        Func<string, string, string?, (int Status, string ContentType, byte[] Body)> apiHandler)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _webRoot = webRoot;
        _apiHandler = apiHandler;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleClient(client), CancellationToken.None);
        }

        _listener.Stop();
    }

    private void HandleClient(TcpClient client)
    {
        try
        {
            using var stream = client.GetStream();
            stream.ReadTimeout = 15000;
            stream.WriteTimeout = 15000;

            var (method, path, body) = ReadRequest(stream);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var (status, contentType, payload) = Route(method, path, body);
            WriteResponse(stream, status, contentType, payload, method == "HEAD");
        }
        catch
        {
            // Una conexión rota no debe tumbar el panel.
        }
        finally
        {
            client.Close();
        }
    }

    private (int Status, string ContentType, byte[] Body) Route(string method, string path, string? body)
    {
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return _apiHandler(method, path, body);
        }

        return ServeStatic(path);
    }

    private (int, string, byte[]) ServeStatic(string path)
    {
        if (path == "/" || path.Length == 0)
        {
            path = "/index.html";
        }

        var relative = Uri.UnescapeDataString(path.TrimStart('/'));
        if (relative.Contains("..", StringComparison.Ordinal))
        {
            return (400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Ruta inválida"));
        }

        var file = Path.Combine(_webRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(file))
        {
            return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("No encontrado"));
        }

        var contentType = Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };

        return (200, contentType, File.ReadAllBytes(file));
    }

    private static (string Method, string Path, string? Body) ReadRequest(NetworkStream stream)
    {
        using var buffer = new MemoryStream();
        var single = new byte[1];
        var tail = new byte[4];

        while (buffer.Length < 64 * 1024)
        {
            var read = stream.Read(single, 0, 1);
            if (read == 0)
            {
                return ("", "", null);
            }

            buffer.WriteByte(single[0]!);

            if (buffer.Length >= 4)
            {
                var arr = buffer.GetBuffer();
                var len = (int)buffer.Length;
                tail[0] = arr[len - 4];
                tail[1] = arr[len - 3];
                tail[2] = arr[len - 2];
                tail[3] = arr[len - 1];

                if (tail[0] == 13 && tail[1] == 10 && tail[2] == 13 && tail[3] == 10)
                {
                    break;
                }
            }
        }

        var headerText = Encoding.ASCII.GetString(buffer.ToArray());
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
        {
            return ("", "", null);
        }

        var method = requestLine[0].ToUpperInvariant();
        var path = requestLine[1];

        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[15..].Trim(), out contentLength);
            }
        }

        string? body = null;
        if (contentLength > 0 && contentLength <= 1024 * 1024)
        {
            var payload = new byte[contentLength];
            var offset = 0;
            while (offset < contentLength)
            {
                var read = stream.Read(payload, offset, contentLength - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            body = Encoding.UTF8.GetString(payload, 0, offset);
        }

        return (method, path, body);
    }

    private static void WriteResponse(NetworkStream stream, int status, string contentType, byte[] body, bool headOnly)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            405 => "Method Not Allowed",
            409 => "Conflict",
            500 => "Internal Server Error",
            _ => "OK"
        };

        var header = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(headerBytes, 0, headerBytes.Length);
        if (!headOnly)
        {
            stream.Write(body, 0, body.Length);
        }

        stream.Flush();
    }

    public static (int, string, byte[]) Json(object value, int status = 200) =>
        (status, "application/json; charset=utf-8",
            JsonSerializer.SerializeToUtf8Bytes(value, Core.Configuration.AppConfig.SerializerOptions));
}
