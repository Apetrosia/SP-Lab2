using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        string[] prefixes = { "http://localhost:8080/" };
        string rootDirectory = args.Length > 0 ? args[0] : null;

        using var cts = new CancellationTokenSource();
        var serverTask = StartServerAsync(prefixes, rootDirectory, cts.Token);

        Console.WriteLine("Server started. Press Enter to stop...");
        Console.ReadLine();
        cts.Cancel();

        await serverTask;
        Console.WriteLine("Server stopped gracefully.");
    }

    static async Task StartServerAsync(string[] prefixes, string rootDirectory, CancellationToken cancellationToken)
    {
        if (prefixes == null || prefixes.Length == 0)
            throw new ArgumentException("prefixes");

        rootDirectory = rootDirectory ?? Directory.GetCurrentDirectory();
        string logFile = "requests.log";
        object logLock = new object();

        using var listener = new HttpListener();
        foreach (string s in prefixes)
            listener.Prefixes.Add(s);

        using var registration = cancellationToken.Register(() => listener.Stop());

        listener.Start();
        Console.WriteLine($"Listening... (root: {rootDirectory})");

        var activeTasks = new List<Task>();
        var tasksLock = new object();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 995)
                {
                    break;
                }

                var task = HandleRequestAsync(context, rootDirectory, logFile, logLock);
                lock (tasksLock)
                {
                    activeTasks.Add(task);
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] New task started. Active tasks: {activeTasks.Count}");
                }

                _ = task.ContinueWith(t =>
                {
                    lock (tasksLock)
                    {
                        activeTasks.Remove(t);
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Task completed. Active tasks: {activeTasks.Count}");
                    }
                }, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error in accept loop: {ex}");
        }
        finally
        {
            if (listener.IsListening)
                listener.Stop();

            Console.WriteLine($"Waiting for {activeTasks.Count} tasks to complete...");
            Task[] tasksToWait;
            lock (tasksLock)
            {
                tasksToWait = activeTasks.ToArray();
            }
            await Task.WhenAll(tasksToWait).ConfigureAwait(false);
            Console.WriteLine("All tasks completed.");
        }
    }

    static async Task HandleRequestAsync(HttpListenerContext context, string rootDirectory, string logFile, object logLock)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        int statusCode = 200;
        try
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                await SendErrorPageAsync(response, "405 - Method Not Allowed");
                statusCode = 405;
            }
            else
            {
                string path = request.Url.AbsolutePath.TrimStart('/');
                if (request.Url.AbsolutePath == "/")
                    path = "index.html";

                string fullPath = Path.GetFullPath(Path.Combine(rootDirectory, path));

                if (!fullPath.StartsWith(rootDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 404;
                    response.StatusDescription = "Not Found";
                    await SendErrorPageAsync(response, "404 - Not Found");
                    statusCode = 404;
                }
                else if (File.Exists(fullPath))
                {
                    await Task.Delay(5000).ConfigureAwait(false);

                    await SendFileAsync(response, fullPath);
                    statusCode = 200;
                }
                else
                {
                    response.StatusCode = 404;
                    response.StatusDescription = "Not Found";
                    await SendErrorPageAsync(response, "404 - Not Found");
                    statusCode = 404;
                }
            }
        }
        catch (Exception ex) when (IsConnectionAbortError(ex))
        {
            Console.WriteLine($"Client aborted connection: {ex.Message}");
            statusCode = 499;
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            response.StatusDescription = "Internal Server Error";
            try
            {
                await SendErrorPageAsync(response, "500 - Internal Server Error");
            }
            catch (Exception innerEx) when (IsConnectionAbortError(innerEx))
            {
                Console.WriteLine($"Client aborted during error page: {innerEx.Message}");
            }
            catch (Exception innerEx)
            {
                Console.WriteLine($"Failed to send error page: {innerEx}");
            }
            statusCode = 500;
            Console.WriteLine($"Error: {ex}");
        }
        finally
        {
            try
            {
                response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: failed to close response stream: {ex.Message}");
            }

            WriteLog(request, statusCode, logFile, logLock);
        }
    }

    private static bool IsConnectionAbortError(Exception ex)
    {
        return ex is ObjectDisposedException ||
               (ex is HttpListenerException hle && hle.ErrorCode == 995) ||
               (ex is IOException && ex.InnerException is HttpListenerException ihle && ihle.ErrorCode == 995);
    }

    static async Task SendFileAsync(HttpListenerResponse response, string filePath)
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
            await fs.CopyToAsync(response.OutputStream).ConfigureAwait(false);
        }
    }

    static async Task SendErrorPageAsync(HttpListenerResponse response, string message)
    {
        byte[] buffer = Encoding.UTF8.GetBytes($"<html><body><h1>{message}</h1></body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
    }

    static void WriteLog(HttpListenerRequest request, int statusCode, string logFile, object logLock)
    {
        string ip = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        string path = request.Url.AbsolutePath;
        string date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string method = request.HttpMethod;
        string logLine = $"{date} | {method} | IP: {ip} | Path: {path} | Code: {statusCode}";

        lock (logLock)
        {
            File.AppendAllLines(logFile, new[] { logLine });
        }
        Console.WriteLine(logLine);
    }
}