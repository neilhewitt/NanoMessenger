using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NanoMessenger
{
    public static class TcpClientExtensions
    {
        public static async Task ConnectAsync(this TcpClient client, IPAddress ipAddress, int port, int timeoutInMilliseconds)
        {
            await ConnectAsync(client, ipAddress.ToString(), port, timeoutInMilliseconds);
        }

        public static async Task ConnectAsync(this TcpClient client, string IPAddressAsString, int port, int timeoutInMilliseconds)
        {
            Task delayTask = Task.Delay(timeoutInMilliseconds);
            Task connectTask = client.ConnectAsync(IPAddressAsString, port);

            await Task.WhenAny(connectTask, delayTask);
            if (delayTask.IsCompleted)
            {
                // we timed out.. but let's check if we connected *just* as we timed out
                if (connectTask.IsCompleted && client.Connected)
                {
                    return;
                }

                throw new TimeoutException("TcpClient.ConnectAsync operation timed out.");
            }
        }
    }
}
