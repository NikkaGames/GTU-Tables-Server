using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

class GTUTablesServer
{
    private TcpListener listener;
    public static byte[] TableBytes;
    public static bool isFetching = true;

    public GTUTablesServer(IPAddress ipAddress, int port)
    {
        listener = new TcpListener(ipAddress, port);
    }

    public static bool Contains(string input, string target)
    {
        return input.Contains(target);
    }

    public static bool Equals(string first, string second)
    {
        return string.Equals(first, second);
    }

    public static string Encrypt(string input, string key)
    {
        if (string.IsNullOrEmpty(key)) return input;
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            int shift = key[i % key.Length];
            char encrypted = (char)((input[i] + shift) & 0xFFFF);
            sb.Append(encrypted);
        }
        return sb.ToString();
    }

    public static string Decrypt(string input, string key)
    {
        if (string.IsNullOrEmpty(key)) return input;
        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            int shift = key[i % key.Length];
            char decrypted = (char)((input[i] - shift) & 0xFFFF);
            sb.Append(decrypted);
        }
        return sb.ToString();
    }
    public static string GetMins()
    {
        return DateTime.Now.Minute.ToString("D2");
    }

    public static string GetTime()
    {
        int hour = DateTime.Now.Hour;
        if (hour < 9 || hour > 22) return "0-Unknown";
        int index = hour - 8;
        return $"{index}-{hour}:00";
    }

    public static string GetNextTime()
    {
        int nextHour = DateTime.Now.Hour + 1;
        if (nextHour > 22) return "0-Unknown";
        int index = nextHour - 8;
        return $"{index}-{nextHour}:00";
    }

    public static string GetDay()
    {
        string[] days = {
            "კვი./Sun.",
            "ორშ./Mon.",
            "სამშ./Tues.",
            "ოთხშ./Wed.",
            "ხუთშ./Thurs.",
            "პარ./Fri.",
            "შაბ./Sat."
        };
        int dayIndex = (int)DateTime.Now.DayOfWeek;
        return days[dayIndex];
    }

    public static async Task<string> FetchUrlContentAsync(string url)
    {
        try
        {
            using HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(120);
            return await client.GetStringAsync(url);
        }
        catch (Exception ex)
        {
            return ex.ToString();
        }
    }

    private async void FetcherThread()
    {
        while (true)
        {
            try
            {
                isFetching = true;
                string first = await FetchUrlContentAsync("https://leqtori.gtu.ge/");
                string first_split = $"{first.Substring(first.IndexOf("prof_teachers.html\" target=\"_blank\"><strong>rs</strong></a></p><p class=\"ql-align-center\"><a href=\"") + 99)}";
                string table_data = await FetchUrlContentAsync($"{first_split.Substring(0, first_split.IndexOf(".html\"") + 5)}");
                TableBytes = Encoding.Default.GetBytes(table_data);
                isFetching = false;
            } catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
            Thread.Sleep(7200 * 1000); // 2 hours I guess :D
        }
    }

    public static async Task<string> GetData(string tid)
    {
        string df = Encoding.UTF8.GetString(TableBytes);
        string target = $"{tid}</th></tr>";
        int index = df.IndexOf(target);
        if (index == -1) return "NOT_FOUND";

        string dff = df.Substring(Math.Max(0, index - 129));
        string dfff = dff.Substring(0, Math.Max(0, dff.IndexOf("#top") - 31));

        string baseHtml = @"
<html xmlns=""http://www.w3.org/1999/xhtml"" lang=""ge"" xml:lang=""ge"">
<head>
<meta http-equiv=""Content-Type"" content=""text/html; charset=UTF-8"">
<link rel=""stylesheet"" href=""https://cdnjs.cloudflare.com/ajax/libs/font-awesome/4.7.0/css/font-awesome.min.css"">
<style>
@import url(https://fonts.googleapis.com/css?family=Open+Sans);
.inf{zoom: 2.5; display: flex;}
.fa{padding: 0px;font-size: 36px;width: auto;height: auto;text-align: center;text-decoration: none;border-radius: 50%; margin-left: 8px;}
.fa-facebook{width: 30px; height: 30px; border-radius: 50%; align-items: center; display: flex; justify-content: center;background: #3B5998;color: white;}
.fa-telegram{width: 30px; height: 30px; border-radius: 50%; align-items: center; display: flex; justify-content: center;background: #25AFE2;color: white;}
.fa-instagram{width: 30px; height: 30px; border-radius: 50%; align-items: center; display: flex; justify-content: center;background: #FF3ED8;color: white;}
.fa-github{width: 30px; height: 30px;border-radius: 50%;align-items: center; display: flex; justify-content: center;background: #666666;color: white;}
.center-div {align-items: center; justify-content: center; display: flex; font-size: 30px; font-family: 'Open Sans'; max-width: fit-content; margin-left: auto;margin-right: auto;background-color: #121212; color: #ffffff;}
table {border-collapse: collapse;font-family: 'Open Sans'; background-color: #1E1E1E; color: #ffffff;}
table td {padding: 8px; color: #ffffff; border: 1px solid #444;}
table thead td {background-color: #333333;color: #ffffff;font-weight: bold;font-size: 14px;border: 1px solid #444;}
table tbody td {color: #ffffff;border: 1px solid #555;}
table tbody tr {background-color: #1E1E1E;}
table tbody tr:nth-child(odd) {background-color: #2A2A2A;}
.lecture {background-color: #1c7cd0; color: #fff;}
.lab {background-color: #52aa35; color: #fff;}
.practical {background-color: #de8f18; color: #fff;}
.practic {background-color: #c65316; color: #fff;}
.seminar {background-color: #761e8f; color: #fff;}
.course {background-color: #bc2a2a; color: #fff;}
body { background-color: #121212; color: #ffffff; }
</style>
</head>
<body>";

        string day = GetDay();

        string tval = dfff;
        if (!day.Equals("კვი./Sun."))
        {
            int mins = int.Parse(GetMins());
            string time = GetTime();
            string nextTime = GetNextTime();

            if (mins >= 45)
                tval = tval.Replace($"<th class=\"yAxis\">{time}</th>", $"<th class=\"yAxis\"><font color=\"orange\">{time}<br>(ENDING SOON)</font></th>");
            else
                tval = tval.Replace($"<th class=\"yAxis\">{time}</th>", $"<th class=\"yAxis\"><font color=\"red\">{time}<br>(NOW)</font></th>");

            if (mins >= 55)
                tval = tval.Replace($"<th class=\"yAxis\">{nextTime}</th>", $"<th class=\"yAxis\"><font color=\"#009DD7\">{nextTime}<br>(STARTING SOON)</font></th>");
            else
                tval = tval.Replace($"<th class=\"yAxis\">{nextTime}</th>", $"<th class=\"yAxis\"><font color=\"green\">{nextTime}<br>(NEXT)</font></th>");

            tval = tval.Replace($"<th class=\"xAxis\">{day}</th>", $"<th class=\"xAxis\"><font color=\"red\">{day} (TODAY)</font></th>");
        }

        string footer = @"
<br><br>
<div class=""center-div"">
<b>App Creator:</b><br><br>
<div class=""inf"">
<a href=""https://t.me/s/NikkaGamesOfficial"" class=""fa fa-telegram""></a>
<a href=""https://facebook.com/sunflower.thrust"" class=""fa fa-facebook""></a>
<a href=""https://www.instagram.com/pmicdxe"" class=""fa fa-instagram""></a>
<a href=""https://github.com/NikkaGames"" class=""fa fa-github""></a>
</div>
</div>
</body></html>";

        return baseHtml + tval + footer;
    }

    public void Start()
    {
        Thread fetchThread = new Thread(FetcherThread);
        fetchThread.Start();
        listener.Start();
        Console.WriteLine($"Listening...");
        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            IPEndPoint endPoint = (IPEndPoint)client.Client.RemoteEndPoint;
            IPAddress address = endPoint.Address;
            int port = endPoint.Port;
#if DEBUG
            Console.WriteLine($"Request from {address}:{port}");
#endif
            Thread clientThread = new Thread(HandleClient);
            clientThread.Start(client);
        }
    }

    public async void HandleClient(object clientObj)
    {
        TcpClient client = (TcpClient)clientObj;
        NetworkStream stream = client.GetStream();
        try
        {
            while (true)
            {
                if (stream.CanRead)
                {
                    byte[] dtsize = new byte[1];
                    StringBuilder sbbb = new StringBuilder();

                    for (int i = 0; i < 64; i++)
                    {
                        stream.Read(dtsize, 0, 1);
                        if (Convert.ToChar(dtsize[0]) == 'L') break;
                        sbbb.Append(Encoding.UTF8.GetString(dtsize));
                    }

                    int sizz = 0;
                    try
                    {
                        sizz = int.Parse(sbbb.ToString());
                    }
                    catch (FormatException wxx)
                    {
                        stream.Flush();
                        break;
                    }

                    byte[] dataBuffer = new byte[sizz];
                    int totalBytesRead = 0;
                    while (totalBytesRead < sizz)
                    {
                        int bytesToRead = sizz - totalBytesRead;
                        int bytesRead = await stream.ReadAsync(dataBuffer, totalBytesRead, bytesToRead);
                        if (bytesRead == 0)
                        {
#if DEBUG
                            Console.WriteLine("Client disconnected during data transmission");
#endif
                            break;
                        }
                        totalBytesRead += bytesRead;
                    }

                    stream.Flush();

                    string tempp = Encoding.UTF8.GetString(dataBuffer);

                    if (tempp != null)
                    {
                        string message = Decrypt(tempp, "table");
#if DEBUG
                        Console.WriteLine("Requested: " + message);
#endif
                        while (isFetching);
                        string response = await GetData(message);
                        response = Encrypt(response, "table");
                        byte[] tosend = Encoding.UTF8.GetBytes(Encoding.UTF8.GetBytes(response).Length + "L" + response);
                        stream.Write(tosend, 0, tosend.Length);
                        stream.Flush();
#if DEBUG
                        if (response.Length >= 32)
                            Console.WriteLine($"Sent {tosend.Length} bytes of data: {Encoding.UTF8.GetString(tosend).Substring(0, 32)} (First 32 bytes).");
                        else Console.WriteLine($"Sent {tosend.Length} bytes of data.");
#endif
                    }
                    else break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());
            return;
        }
        finally
        {
#if DEBUG
            Console.WriteLine("Client disconnected.");
#endif
        }
    }
}

