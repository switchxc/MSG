using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Text.Json.Serialization.Metadata;

class Program
{
    const int PORT = 8888;
    static TcpListener server;
    static readonly ConcurrentDictionary<TcpClient, ClientInfo> clients = new();
    static readonly ConcurrentDictionary<string, IPEndPoint> nickToEndpoint = new();
    static readonly HashSet<string> bannedIPs = new();
    static string serverNickname = "Сервер";
    static int inputLine = 0;
    static volatile bool isRunning = true;
    static readonly CancellationTokenSource cts = new();
    static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static async Task Main()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;

            var radminIp = Dns.GetHostEntry(Dns.GetHostName())
                .AddressList.FirstOrDefault(ip =>
                    ip.AddressFamily == AddressFamily.InterNetwork &&
                    ip.ToString().StartsWith("26.")) ?? IPAddress.Any;

            Console.Write("Введите ваш ник на сервере: ");
            serverNickname = Console.ReadLine() ?? "Сервер";

            server = new TcpListener(radminIp, PORT);
            server.Start();

            PrintHeader();
            inputLine = Console.CursorTop;

            _ = Task.Run(() => HandleServerCommands(cts.Token));

            Console.WriteLine($"Сервер запущен на {radminIp}:{PORT}");
            Console.WriteLine("Ожидание подключений...");

            await AcceptClientsAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка сервера: {ex.Message}");
        }
        finally
        {
            Cleanup();
        }
    }

    static async Task AcceptClientsAsync(CancellationToken token)
    {
        while (isRunning && !token.IsCancellationRequested)
        {
            try
            {
                var client = await server.AcceptTcpClientAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleClient(client, token), token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка принятия клиента: {ex.Message}");
            }
        }
    }

    static async Task HandleClient(TcpClient client, CancellationToken token)
    {
        using NetworkStream stream = client.GetStream();
        try
        {
            var buffer = new byte[4096];
            int received = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (received == 0) return;

            string nickname = Encoding.UTF8.GetString(buffer, 0, received).Trim();
            var endpoint = (IPEndPoint)client.Client.RemoteEndPoint;

            if (bannedIPs.Contains(endpoint.Address.ToString()))
            {
                await SendMessageAsync(stream, "/ban", token);
                return;
            }

            nickToEndpoint[nickname] = endpoint;
            var clientInfo = new ClientInfo(nickname, endpoint.Address.ToString());
            clients[client] = clientInfo;

            var joinMsg = new ChatMessage("Система", $"{nickname} ({clientInfo.IP}) вошёл в чат", DateTime.Now, true);
            PrintMessage(joinMsg);
            await BroadcastMessage(JsonSerializer.Serialize(joinMsg, jsonOptions), token);

            while (isRunning && !token.IsCancellationRequested)
            {
                received = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (received == 0) break;

                var json = Encoding.UTF8.GetString(buffer, 0, received);

                if (json.Contains("\"From\""))
                {
                    var pm = JsonSerializer.Deserialize<PrivateMessage>(json, jsonOptions);
                    await ProcessPrivateMessage(pm, stream, token);
                }
                else
                {
                    var incomingMsg = JsonSerializer.Deserialize<ChatMessage>(json, jsonOptions);
                    var msg = new ChatMessage(nickname, incomingMsg.Text, DateTime.Now, false, clientInfo.IP);
                    PrintMessage(msg);
                    await BroadcastMessage(JsonSerializer.Serialize(msg, jsonOptions), token);
                }
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            Console.WriteLine($"Ошибка клиента: {ex.Message}");
        }
        finally
        {
            HandleClientDisconnect(client);
        }
    }

    static async Task ProcessPrivateMessage(PrivateMessage pm, NetworkStream senderStream, CancellationToken token)
    {
        var recipient = clients.FirstOrDefault(c =>
            c.Value.Nickname.Equals(pm.To, StringComparison.OrdinalIgnoreCase)).Key;

        if (recipient != null)
        {
            await SendMessageAsync(recipient.GetStream(), JsonSerializer.Serialize(pm, jsonOptions), token);
            await SendMessageAsync(senderStream,
                JsonSerializer.Serialize(new ChatMessage("Система", $"Сообщение для {pm.To} отправлено", DateTime.Now, true), jsonOptions),
                token);
        }
        else
        {
            await SendMessageAsync(senderStream,
                JsonSerializer.Serialize(new ChatMessage("Система", $"Пользователь {pm.To} не найден", DateTime.Now, true), jsonOptions),
                token);
        }
    }

    static async Task HandleServerCommands(CancellationToken token)
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
                    string[] parts = input.Split(' ');
                    switch (parts[0].ToLower())
                    {
                        case "/clients":
                            Console.WriteLine("Подключены:");
                            foreach (var c in clients.Values)
                                Console.WriteLine($"- {c.Nickname} ({c.IP})");
                            break;

                        case "/pm":
                            if (parts.Length >= 3)
                            {
                                string message = string.Join(' ', parts.Skip(2));
                                await SendPrivateMessage(parts[1], message, token);
                            }
                            break;

                        case "/kick":
                            if (parts.Length > 1) await KickClient(parts[1], token);
                            break;

                        case "/ban":
                            if (parts.Length > 1) await BanClient(parts[1], token);
                            break;

                        case "/exit":
                            isRunning = false;
                            return;

                        default:
                            Console.WriteLine("Неизвестная команда");
                            break;
                    }
                }
                else
                {
                    var msg = new ChatMessage(serverNickname, input, DateTime.Now, false,
                        ((IPEndPoint)server.LocalEndpoint).Address.ToString());
                    PrintMessage(msg);
                    await BroadcastMessage(JsonSerializer.Serialize(msg, jsonOptions), token);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка команды: {ex.Message}");
            }
        }
    }

    static async Task SendPrivateMessage(string targetNick, string message, CancellationToken token)
    {
        var recipient = clients.FirstOrDefault(c =>
            c.Value.Nickname.Equals(targetNick, StringComparison.OrdinalIgnoreCase)).Key;

        if (recipient != null)
        {
            var pm = new PrivateMessage(serverNickname, targetNick, message, DateTime.Now);
            await SendMessageAsync(recipient.GetStream(), JsonSerializer.Serialize(pm, jsonOptions), token);
            PrintSystemMessage($"Приватно для {targetNick}: {message}");
        }
        else
        {
            PrintSystemMessage($"Пользователь {targetNick} не найден");
        }
    }

    static async Task KickClient(string target, CancellationToken token)
    {
        var clientToKick = clients.FirstOrDefault(c =>
            c.Value.Nickname.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            c.Value.IP.Equals(target)).Key;

        if (clientToKick != null)
        {
            try
            {
                await SendMessageAsync(clientToKick.GetStream(), "/kick", token);
                clients.TryRemove(clientToKick, out _);
                clientToKick.Close();
                PrintSystemMessage($"{target} был отключен");
            }
            catch (Exception ex)
            {
                PrintSystemMessage($"Ошибка отключения: {ex.Message}");
            }
        }
        else
        {
            PrintSystemMessage($"Пользователь {target} не найден");
        }
    }

    static async Task BanClient(string target, CancellationToken token)
    {
        var clientInfo = clients.FirstOrDefault(c =>
            c.Value.Nickname.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            c.Value.IP.Equals(target)).Value;

        if (clientInfo != null)
        {
            bannedIPs.Add(clientInfo.IP);
            await KickClient(target, token);
            PrintSystemMessage($"{target} ({clientInfo.IP}) забанен");
        }
        else
        {
            PrintSystemMessage($"Пользователь {target} не найден");
        }
    }

    static async Task BroadcastMessage(string json, CancellationToken token)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        foreach (var client in clients.Keys.ToList())
        {
            try
            {
                if (client.Connected)
                {
                    await client.GetStream().WriteAsync(data, token).ConfigureAwait(false);
                }
            }
            catch
            {
                clients.TryRemove(client, out _);
            }
        }
    }

    static async Task SendMessageAsync(NetworkStream stream, string message, CancellationToken token)
    {
        byte[] data = Encoding.UTF8.GetBytes(message);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
    }

    static void HandleClientDisconnect(TcpClient client)
    {
        if (clients.TryRemove(client, out var info))
        {
            var leaveMsg = new ChatMessage("Система", $"{info.Nickname} вышел из чат", DateTime.Now, true);
            PrintMessage(leaveMsg);
            _ = BroadcastMessage(JsonSerializer.Serialize(leaveMsg, jsonOptions), cts.Token);
            nickToEndpoint.TryRemove(info.Nickname, out _);
        }
        client.Close();
    }

    static void Cleanup()
    {
        isRunning = false;
        cts.Cancel();
        foreach (var client in clients.Keys.ToList())
        {
            client.Close();
        }
        server?.Stop();
        cts.Dispose();
    }

    static void PrintMessage(ChatMessage msg)
    {
        lock (Console.Out)
        {
            Console.MoveBufferArea(0, inputLine, Console.WindowWidth, 1, 0, inputLine + 1);
            Console.SetCursorPosition(0, inputLine);

            if (msg.IsSystem)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"[{msg.Time:HH:mm:ss}] ");
                Console.WriteLine(msg.Text.PadRight(Console.WindowWidth - 20));
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"[{msg.Time:HH:mm:ss}] ");

                Console.ForegroundColor = msg.Sender == serverNickname ? ConsoleColor.Magenta : ConsoleColor.Cyan;
                Console.Write($"{msg.Sender}{(msg.IP != null ? $" ({msg.IP})" : "")}: ");

                Console.ResetColor();
                Console.WriteLine(msg.Text.PadRight(Console.WindowWidth - 20 - msg.Sender.Length - (msg.IP?.Length ?? 0)));
            }

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

            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"[!] {text}".PadRight(Console.WindowWidth - 5));

            inputLine++;
            Console.SetCursorPosition(0, inputLine);
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
        Console.Write($"{serverNickname}> ");
        Console.ResetColor();
    }

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"╔════════════════════════════════════╗");
        Console.WriteLine($"║ Сервер чата {((IPEndPoint)server.LocalEndpoint).Address}:{PORT} ║");
        Console.WriteLine($"║ Ник: {serverNickname,-25} ║");
        Console.WriteLine($"╚════════════════════════════════════╝");
        Console.ResetColor();
    }
}

record ClientInfo(string Nickname, string IP);
record ChatMessage(string Sender, string Text, DateTime Time, bool IsSystem, string? IP = null);
record PrivateMessage(string From, string To, string Text, DateTime Time);