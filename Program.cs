using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        GTUTablesServer server = new GTUTablesServer(IPAddress.Any, 5000);
        server.Start();
    }
}