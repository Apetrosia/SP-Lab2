using System;
using System.Net;
using System.IO;
using System.Text;
using System.Threading.Tasks;

class Program
{
    static void Main(string[] args)
    {
        string[] prefixes = { "http://localhost:8080/" };
        string rootDirectory = args.Length > 0 ? args[0] : null;
        StartServer(prefixes, rootDirectory);
    }

    static void StartServer(string[] prefixes, string rootDirectory = null)
    {
        if (prefixes == null || prefixes.Length == 0)
            throw new ArgumentException("prefixes");

        rootDirectory = rootDirectory ?? Directory.GetCurrentDirectory();
        string logFile = "requests.log";
        object logLock = new object();

        HttpListener listener = new HttpListener();
        foreach (string s in prefixes)
            listener.Prefixes.Add(s);

        listener.Start();
        Console.WriteLine($"Listening... (root: {rootDirectory})");

        while (true)
        {
            HttpListenerContext context = listener.GetContext();
            Task.Run(() => HandleRequest(context, rootDirectory, logFile, logLock));
        }
    }

    static void HandleRequest(HttpListenerContext context, string rootDirectory, string logFile, object logLock)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        int statusCode = 200;
        try
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                SendErrorPage(response, "405 - Method Not Allowed");
                statusCode = 405;
            }
            else
            {
                string path = request.Url.AbsolutePath.TrimStart('/');

                if (request.Url.AbsolutePath == "/")
                {
                    path = "index.html";
                }

                string fullPath = Path.GetFullPath(Path.Combine(rootDirectory, path));

                if (!fullPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 404;
                    response.StatusDescription = "Not Found";
                    SendErrorPage(response, "404 - Not Found");
                    statusCode = 404;
                }
                else if (File.Exists(fullPath))
                {
                    SendFile(response, fullPath);
                    statusCode = 200;
                }
                else
                {
                    response.StatusCode = 404;
                    response.StatusDescription = "Not Found";
                    SendErrorPage(response, "404 - Not Found");
                    statusCode = 404;
                }
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            response.StatusDescription = "Internal Server Error";
            SendErrorPage(response, "500 - Internal Server Error");
            statusCode = 500;
            Console.WriteLine($"Error: {ex}");
        }
        finally
        {
            WriteLog(request, statusCode, logFile, logLock);
            response.OutputStream.Close();
        }
    }

    static void SendFile(HttpListenerResponse response, string filePath)
    {
        string extension = Path.GetExtension(filePath);
        var mimeTypes = new Dictionary<string, string>
        {
            [".html"] = "text/html",
            [".css"] = "text/css",
            [".js"] = "application/javascript",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".txt"] = "text/plain"
        };

        string mime = mimeTypes.ContainsKey(extension) ? mimeTypes[extension] : "application/octet-stream";
        response.ContentType = mime;

        using (FileStream fs = File.OpenRead(filePath))
        {
            response.ContentLength64 = fs.Length;
            fs.CopyTo(response.OutputStream);
        }
    }

    static void SendErrorPage(HttpListenerResponse response, string message)
    {
        byte[] buffer = Encoding.UTF8.GetBytes($"<html><body><h1>{message}</h1></body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
    }

    static void WriteLog(HttpListenerRequest request, int statusCode, string logFile, object logLock)
    {
        string ip = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        string path = request.Url.AbsolutePath;
        string date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string method = request.HttpMethod;
        //string agent = request.UserAgent ?? "unknown";
        //string logLine = $"{date} | {method} | IP: {ip} | Path: {path} | Code: {statusCode} | Agent: {agent}";
        string logLine = $"{date} | {method} | IP: {ip} | Path: {path} | Code: {statusCode}";

        lock (logLock)
        {
            File.AppendAllLines(logFile, new[] { logLine });
        }
        Console.WriteLine(logLine);
    }
}