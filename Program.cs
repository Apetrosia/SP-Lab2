using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Linq;

class Program
{
    private static int _activeRequests = 0;
    private static readonly object _logLock = new object();
    private static string _logFile = "requests.log"; // Единый файл логов

    static async Task Main(string[] args)
    {
        string[] prefixes = { "http://localhost:8080/" };
        string rootDirectory = args.Length > 0 ? args[0] : null;
        await StartServerAsync(prefixes, rootDirectory);
    }

    static async Task StartServerAsync(string[] prefixes, string rootDirectory = null)
    {
        if (prefixes == null || prefixes.Length == 0)
            throw new ArgumentException("prefixes");

        rootDirectory = rootDirectory ?? Directory.GetCurrentDirectory();

        HttpListener listener = new HttpListener();
        foreach (string s in prefixes)
            listener.Prefixes.Add(s);

        listener.Start();

        // Выводим информацию о запуске
        Console.WriteLine("Listening on:");
        foreach (string prefix in prefixes)
        {
            Console.WriteLine($"  {prefix}");
        }
        Console.WriteLine($"Root directory: {rootDirectory}");
        Console.WriteLine("Press 'q' to stop the server gracefully.");

        // Список запущенных задач обработки запросов
        var runningTasks = new List<Task>();
        var tasksLock = new object();

        // Токен отмены для graceful shutdown
        var shutdownTokenSource = new CancellationTokenSource();

        // Обработка Ctrl+C
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true; // предотвращаем немедленное завершение
            LogServerEvent($"Ctrl+C pressed. Active requests: {_activeRequests}. Initiating shutdown...");
            shutdownTokenSource.Cancel();
        };

        // Обработка завершения процесса (например, закрытие окна)
        AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
        {
            // Здесь уже мало времени, но попробуем залогировать
            string msg = $"Process exiting. Active requests: {_activeRequests}. Trying to shutdown...";
            Console.WriteLine(msg);
            // Попытка записи в файл может не успеть
            try
            {
                lock (_logLock)
                {
                    File.AppendAllLines(_logFile, new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | [SERVER] {msg}" });
                }
            }
            catch { }
            // Принудительно отменяем, но процесс всё равно завершится
            shutdownTokenSource.Cancel();
        };

        // Задача для отслеживания нажатия клавиши 'q'
        var shutdownTask = Task.Run(() =>
        {
            while (!shutdownTokenSource.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.KeyChar == 'q' || key.KeyChar == 'Q')
                    {
                        LogServerEvent($"Shutdown signal received (key 'q'). Active requests: {_activeRequests}. Initiating shutdown...");
                        shutdownTokenSource.Cancel();
                        break;
                    }
                }
                Thread.Sleep(100);
            }
        });

        try
        {
            // Основной цикл приема запросов
            while (!shutdownTokenSource.IsCancellationRequested)
            {
                // Асинхронно ожидаем входящий запрос
                var getContextTask = listener.GetContextAsync();
                // Ждем либо запрос, либо сигнал остановки
                var completedTask = await Task.WhenAny(getContextTask, shutdownTask);
                if (completedTask == shutdownTask || shutdownTokenSource.IsCancellationRequested)
                {
                    // Получен сигнал остановки – выходим из цикла
                    break;
                }

                // Получаем контекст запроса
                var context = await getContextTask;

                // Увеличиваем счетчик активных запросов
                Interlocked.Increment(ref _activeRequests);

                // Запускаем обработку в отдельной задаче
                var processingTask = Task.Run(() => HandleRequestAsync(context, rootDirectory));

                // Добавляем задачу в список активных
                lock (tasksLock)
                {
                    // Удаляем уже завершенные задачи для очистки списка
                    runningTasks.RemoveAll(t => t.IsCompleted);
                    runningTasks.Add(processingTask);
                }

                // Логируем создание таска
                string path = context.Request.Url.AbsolutePath;
                LogServerEvent($"Task started for {path}. Active requests: {_activeRequests}");

                // Добавляем продолжение для логирования завершения задачи
                processingTask.ContinueWith(t =>
                {
                    int remaining = Interlocked.Decrement(ref _activeRequests);
                    LogServerEvent($"Task completed for {path}. Active requests: {remaining}");
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
        }
        catch (OperationCanceledException)
        {
            // Ожидаемая отмена
        }
        finally
        {
            // Останавливаем прием новых запросов
            listener.Stop();
            listener.Close();
            LogServerEvent("Listener stopped. Waiting for pending requests to complete...");

            // Дожидаемся завершения всех активных задач обработки
            Task[] tasksToWait;
            lock (tasksLock)
            {
                tasksToWait = runningTasks.ToArray();
            }
            if (tasksToWait.Length > 0)
                await Task.WhenAll(tasksToWait);

            LogServerEvent($"All requests processed. Server shut down. Final active requests: {_activeRequests}");
        }
    }

    // Асинхронная обработка запроса с искусственной задержкой 5 секунд
    static async Task HandleRequestAsync(HttpListenerContext context, string rootDirectory)
    {
        HttpListenerRequest request = context.Request;
        HttpListenerResponse response = context.Response;

        int statusCode = 200;

        try
        {
            // Искусственная задержка 5 секунд для имитации долгой обработки
            await Task.Delay(5000);

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
            WriteLog(request, statusCode);
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

    static void WriteLog(HttpListenerRequest request, int statusCode)
    {
        string ip = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        string path = request.Url.AbsolutePath;
        string date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string method = request.HttpMethod;
        string logLine = $"{date} | {method} | IP: {ip} | Path: {path} | Code: {statusCode}";

        lock (_logLock)
        {
            File.AppendAllLines(_logFile, new[] { logLine });
        }
        Console.WriteLine(logLine);
    }

    // Логирование серверных событий (в тот же файл, но с префиксом [SERVER])
    static void LogServerEvent(string message)
    {
        string date = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        string logLine = $"{date} | [SERVER] {message}";

        lock (_logLock)
        {
            File.AppendAllLines(_logFile, new[] { logLine });
        }
        Console.WriteLine(logLine);
    }
}