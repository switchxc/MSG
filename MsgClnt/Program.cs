using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

class Program
{
    const int PORT = 8888;
    static string nickname = "Аноним";
    static string serverIp = "26.204.36.62";
    static int inputLine = 0;
    static volatile bool isRunning = true;
    static readonly Dictionary<string, List<PrivateMessage>> messageHistory = new();
    static readonly CancellationTokenSource cts = new();
    static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;

        try
        {
            Console.Write("Введите ваш ник: ");
            nickname = await Task.Run(() => Console.ReadLine() ?? "Аноним");

            Console.Write("Введите IP сервера: ");
            var inputIp = await Task.Run(() => Console.ReadLine());
            if (!string.IsNullOrEmpty(inputIp)) serverIp = inputIp;

            PrintHeader();
            inputLine = Console.CursorTop;

            using var client = new TcpClient();
            await client.ConnectAsync(serverIp, PORT).ConfigureAwait(false);
            using var stream = client.GetStream();

            await SendMessageAsync(stream, nickname, cts.Token);

            _ = Task.Run(() => ReceiveMessages(stream, cts.Token));

            await HandleUserInput(stream, cts.Token);
        }
        catch (Exception ex)
        {
            PrintSystemMessage($"Ошибка: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    static async Task HandleUserInput(NetworkStream stream, CancellationToken token)
    {
        while (isRunning && !token.IsCancellationRequested)
        {
            try
            {
                string input = await ReadLineWithClear();
                if (string.IsNullOrEmpty(input)) continue;

                ClearInputLine();

                if (input.StartsWith("/"))
                {
                    await ProcessCommand(input, stream, token);
                }
                else
                {
                    await SendChatMessage(input, stream, token);
                }
            }
            catch (Exception ex)
            {
                PrintSystemMessage($"Ошибка ввода: {ex.Message}");
            }
        }
    }

    static async Task ReceiveMessages(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[4096];
        try
        {
            while (isRunning && !token.IsCancellationRequested)
            {
                int received = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (received == 0) break;

                var json = Encoding.UTF8.GetString(buffer, 0, received);

                if (json == "/kick")
                {
                    PrintSystemMessage("Сервер отключил вас");
                    isRunning = false;
                }
                else if (json == "/ban")
                {
                    PrintSystemMessage("Вы были забанены на сервере");
                    isRunning = false;
                }
                else
                {
                    try
                    {
                        if (json.Contains("\"From\""))
                        {
                            var pm = JsonSerializer.Deserialize<PrivateMessage>(json, jsonOptions)!;
                            PrintPrivateMessage(pm);

                            if (!messageHistory.ContainsKey(pm.From))
                                messageHistory[pm.From] = new List<PrivateMessage>();
                            messageHistory[pm.From].Add(pm);
                        }
                        else
                        {
                            var msg = JsonSerializer.Deserialize<ChatMessage>(json, jsonOptions)!;
                            if (msg.IsSystem)
                            {
                                PrintSystemMessage(msg.Text);
                            }
                            else if (msg.Sender != nickname) // Игнорируем свои сообщения
                            {
                                PrintMessage(msg);
                            }
                        }
                    }
                    catch (JsonException ex)
                    {
                        PrintSystemMessage($"Ошибка разбора сообщения: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (isRunning) PrintSystemMessage($"Ошибка соединения: {ex.Message}");
        }
        finally
        {
            isRunning = false;
        }
    }

    static async Task ProcessCommand(string command, NetworkStream stream, CancellationToken token)
    {
        string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (parts[0].ToLower())
        {
            case "/pm":
                if (parts.Length >= 3)
                {
                    string message = string.Join(' ', parts.Skip(2));
                    var pm = new PrivateMessage(nickname, parts[1], message, DateTime.Now);
                    await SendMessageAsync(stream, JsonSerializer.Serialize(pm, jsonOptions), token);

                    if (!messageHistory.ContainsKey(parts[1]))
                        messageHistory[parts[1]] = new List<PrivateMessage>();
                    messageHistory[parts[1]].Add(pm);

                    PrintPrivateMessage(new PrivateMessage("Вы", parts[1], message, DateTime.Now));
                }
                break;

            case "/history":
                if (parts.Length > 1 && messageHistory.TryGetValue(parts[1], out var history))
                {
                    Console.WriteLine($"История с {parts[1]}:");
                    foreach (var msg in history)
                        Console.WriteLine($"[{msg.Time:HH:mm}] {msg.From}: {msg.Text}");
                }
                else
                {
                    PrintSystemMessage("История не найдена");
                }
                break;

            case "/exit":
                isRunning = false;
                break;

            default:
                PrintSystemMessage("Неизвестная команда");
                break;
        }
    }

    static async Task SendChatMessage(string text, NetworkStream stream, CancellationToken token)
    {
        var msg = new ChatMessage(nickname, text, DateTime.Now, false);
        await SendMessageAsync(stream, JsonSerializer.Serialize(msg, jsonOptions), token);
        PrintMessage(msg); // Отображаем свое сообщение локально
    }

    static async Task SendMessageAsync(NetworkStream stream, string message, CancellationToken token)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
    }

    static void PrintMessage(ChatMessage msg)
    {
        lock (Console.Out)
        {
            Console.MoveBufferArea(0, inputLine, Console.WindowWidth, 1, 0, inputLine + 1);
            Console.SetCursorPosition(0, inputLine);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"[{msg.Time:HH:mm:ss}] ");

            Console.ForegroundColor = msg.Sender == nickname ? ConsoleColor.Magenta : ConsoleColor.Cyan;
            Console.Write($"{msg.Sender}: ");

            Console.ResetColor();
            Console.WriteLine(msg.Text.PadRight(Console.WindowWidth - 20 - msg.Sender.Length));

            inputLine++;
            ShowInputPrompt();
        }
    }

    static void PrintPrivateMessage(PrivateMessage pm)
    {
        lock (Console.Out)
        {
            Console.MoveBufferArea(0, inputLine, Console.WindowWidth, 1, 0, inputLine + 1);
            Console.SetCursorPosition(0, inputLine);

            Console.ForegroundColor = ConsoleColor.DarkMagenta;
            Console.Write($"[ЛС {pm.From}->{pm.To} {pm.Time:HH:mm:ss}] ");
            Console.ResetColor();
            Console.WriteLine(pm.Text.PadRight(Console.WindowWidth - 30));

            inputLine++;
            ShowInputPrompt();
        }
    }

    static void PrintSystemMessage(string text)
    {
        lock (Console.Out)
        {
            Console.MoveBufferArea(0, inputLine, Console.WindowWidth, 1, 0, inputLine + 1);
            Console.SetCursorPosition(0, inputLine);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Сервер] {text}".PadRight(Console.WindowWidth - 10));

            inputLine++;
            ShowInputPrompt();
        }
    }

    static async Task<string> ReadLineWithClear()
    {
        StringBuilder input = new StringBuilder();
        int left = Console.CursorLeft;
        int top = Console.CursorTop;

        while (true)
        {
            var key = Console.ReadKey(true);
            if (key.Key == ConsoleKey.Enter) break;

            if (key.Key == ConsoleKey.Backspace && input.Length > 0)
            {
                input.Remove(input.Length - 1, 1);
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                input.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }

        return input.ToString();
    }

    static void ClearInputLine()
    {
        int currentLeft = Console.CursorLeft;
        int currentTop = Console.CursorTop;

        Console.SetCursorPosition(0, currentTop);
        Console.Write(new string(' ', Console.WindowWidth - 1));
        Console.SetCursorPosition(0, currentTop);
        ShowInputPrompt();
    }

    static void ShowInputPrompt()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"{nickname}> ");
        Console.ResetColor();
    }

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Blue;
        Console.WriteLine($"╔══════════════════════════════╗");
        Console.WriteLine($"║ Ник: {nickname,-20} ║");
        Console.WriteLine($"║ Сервер: {serverIp,-16} ║");
        Console.WriteLine($"╚══════════════════════════════╝");
        Console.ResetColor();
    }

    static void Cleanup()
    {
        isRunning = false;
        cts.Cancel();
        Console.CursorVisible = true;
        cts.Dispose();
    }
}

record ChatMessage(string Sender, string Text, DateTime Time, bool IsSystem, string? IP = null);
record PrivateMessage(string From, string To, string Text, DateTime Time);