﻿using ClientSubnautica.MultiplayerManager.ReceiveData;
using Newtonsoft.Json.Linq;
using ServerSubnautica;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

class Server
{
    public static readonly object _lock = new object();
    public static readonly Dictionary<string, TcpClient> list_clients = new Dictionary<string, TcpClient>();
    public static readonly Dictionary<string, string> list_nicknames = new Dictionary<string, string>();
    public static byte[] mapBytes;
    public static string mapName;
    public static JObject configParams;
    public static JObject gameInfo;
    private static string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
    public static string mapPath;
    public static string gameInfoPath;

    static void Main(string[] args)
    {
        // Logging to file -- TEST / DO NOT TOUCH (but for working improvements)
        string logsPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "logs.log");
        FileStream filestream = new FileStream(logsPath, FileMode.Create);
        StreamWriter writer = new StreamWriter(filestream);
        writer.AutoFlush = true;
        Console.SetOut(writer);
        Console.SetError(writer);
        // END OF LOGGING

        Server server = new Server();
        configParams = server.LoadParam(configPath);
        

        mapName = configParams["MapFolderName"].ToString();
        gameInfoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mapName, "gameinfo.json");
        mapPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, mapName + ".zip");
        gameInfo = server.LoadParam(gameInfoPath);
        RefreshLoadSaves();

        string ipAddress = configParams["ipAddress"].ToString();
        int port = int.Parse(configParams["port"].ToString());

        IPAddress host = IPAddress.Parse(ipAddress);
        TcpListener ServerSocket = new TcpListener(host, port);
        ServerSocket.Start();
        Console.WriteLine("Listening on "+ ipAddress + ":"+port);

        while (true)
        {
            try
            {
                TcpClient client = ServerSocket.AcceptTcpClient();

                // System to receive the ID
                string id = "";
                string username = "";
                byte[] buffer = new byte[1024];
                int byte_count;
                byte_count = client.GetStream().Read(buffer, 0, buffer.Length);
                string data = Encoding.ASCII.GetString(buffer, 0, byte_count);
                if (!data.Contains("/END/"))
                    continue;
                // Split the greaaaat stream into commands
                string[] commands = data.Split(new string[] { "/END/" }, StringSplitOptions.None);
                foreach (string command in commands)
                {
                    if (command.Length <= 1)
                        continue;
                    // Try to see if this command contains an ID
                    try
                    {
                        string idCMD = command.Split(':')[0]; // A command looks like this globally: "9:6486198964615684:/END/" that's the scheme
                        if (idCMD == NetworkCMD.getIdCMD("ReceivingID")) // Check if the command type is the one to receive an ID.
                        {
                            id = command.Split(':')[1]; // If yes, then as we can see the id is just after 9: so [1].
                            username = command.Split(":")[2]; //
                            Console.WriteLine($"Server received a new ID from an entering connection: {id} with name {username}");
                            break;
                        }
                        else
                        {
                            new ClientMethod().redirectCall(command.Split(":"), command.Split(":")[1]);
                        }
                    }
                    catch (Exception e)
                    {
                        throw new Exception(e.Message, e);
                    }
                }
                // This adds the new client and its ID to the list, now that ID have been defined higher.
                lock (_lock)
                {
                    if (list_clients.ContainsKey(id))
                    {
                        TcpClient oldClient;
                        if (list_clients.TryGetValue(id, out oldClient))
                        {
                            try
                            {
                                oldClient.Close();
                            }
                            catch (Exception e)
                            {
                                Console.WriteLine("Re-entering id:" + id + " Error when close old TcpClient, ignoring.");
                            }
                        }
                        list_clients.Remove(id);
                    }
                    list_clients.Add(id, client);
                    if (list_nicknames.ContainsKey(id))
                    {
                        list_nicknames.Remove(id);
                    }
                    list_nicknames.Add(id, username);
                }
                Console.WriteLine($"Someone connected, id: {id}, username: {username}");

                Thread receiveThread = new Thread(new HandleClient(id).start);
                receiveThread.Start();
                Thread.Sleep(5);
            }
            catch (Exception e)
            {
                Console.WriteLine("Receive package error:");
                Console.WriteLine(e);
            }
        }
    }

    public static void RefreshLoadSaves()
    {
        if (!zipFile(mapName))
        {
            Console.WriteLine("Can't compress world");
            Console.WriteLine("Press a key...");
            Console.ReadKey();
            Environment.Exit(1);
        }
        mapBytes = getFileBytes(mapPath);
        Console.WriteLine("Load save data, length: " + mapBytes.Length);
        File.Delete(mapPath);
    }

    public JObject LoadParam(string path)
    {
        if (File.Exists(path))
        {
            return JObject.Parse(File.ReadAllText(path)); // Parse to a useable object.
        }
        else if (path.EndsWith("config.json")) // Check if the file we're looking for is the config.json file
        {
            // If the file we're looking for does not exist, then a ne one is created with default values.
            File.WriteAllTextAsync(path,
@"{
    ""MapFolderName"": ""slot0000"",
    ""ipAddress"": """ + GetLocalIPv4() + @""",
    ""port"": 5000
}");
            return JObject.Parse(File.ReadAllText(path));
        } // If it is not the config.json file, it throws a new exception.
        else throw new Exception($"The file you're trying to access ({Path.GetFileName(path)}) does not exist or is inaccessible and has no default value.");
    }


    public static bool zipFile(string folderName)
    {
        try
        {
            string[] paths = { AppDomain.CurrentDomain.BaseDirectory, folderName };
            string fullPath = Path.Combine(paths);

            string[] outPath = { AppDomain.CurrentDomain.BaseDirectory, folderName + ".zip" };
            string outFullPath = Path.Combine(outPath);
            string startPath = fullPath;
            string zipPath = outFullPath;

            if (File.Exists(zipPath))
            {
                File.Delete(zipPath);
            }

            ZipFile.CreateFromDirectory(startPath, zipPath);
            return true;
        }
        catch(Exception e)
        {
            Console.WriteLine(e);
            return false;
        }
    }

    public static byte[] getFileBytes(string path)
    {
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// Gets the IPv4 of this computer. It will be a 25... if using Hamachi, for example.
    /// </summary>
    /// <returns>A string of IP Address (type IPv4)</returns>
    public static string GetLocalIPv4()
    {
        if (!NetworkInterface.GetIsNetworkAvailable()) 
            return null;

        IPHostEntry host = Dns.GetHostEntry(Dns.GetHostName());

        return host.AddressList.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToString();
    }
}