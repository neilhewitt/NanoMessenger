using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NanoMessenger
{
    public class Messenger : IDisposable
    {
        private Task _processMessagesTask;
        private Task _pingTask;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;

        private bool _disposed;
        private bool _connected;
        private bool _disconnecting;
        private bool _listening;
        private bool _threadShouldDie;

        private byte[] _buffer = new byte[32768];
        private string _data = String.Empty;

        private int _connectionAttempts;
        private DateTime _lastPingTime = DateTime.Now;

        private List<QueueItem> _messageQueue = new List<QueueItem>();

        public IPAddress EndPointAddress { get; private set; }
        public string HostName { get; private set; }
        public ushort Port { get; private set; }
        public MessengerType Type { get; private set; }
        public bool Connected => _connected;
        public int ConnectionAttemptsSinceDisconnect => _connectionAttempts;
        public bool PingEnabled { get; set; } = true;

        public event EventHandler<Message> OnReceiveMessage;
        public event EventHandler<Guid> OnReceiveAcknowledge;
        public event EventHandler OnConnecting;
        public event EventHandler<string> OnConnectionRetry;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;

        public void Open()
        {
            if (!_connected)
            {
                if (Type == MessengerType.Receive)
                {
                    _listener = new TcpListener(IPAddress.Any, Port);
                    _listener.Start();
                    _listening = true;
                }

                if ((_processMessagesTask == null && _pingTask == null) || (_processMessagesTask.IsCompleted && _pingTask.IsCompleted))
                {
                    _processMessagesTask = Task.Run(ProcessMessageLoop);
                    _pingTask = Task.Run(PingLoop);
                }
            }
        }

        public void Close()
        {
            _threadShouldDie = true;
            _disconnecting = true;
            _stream?.Close();
            _client?.Close();
            _client = null;
            _stream = null;
            _listener?.Stop();
            _listener = null;
            _disconnecting = false;
            _connected = false;
        }

        public void QueueMessage(string text, Action<string> callback = null)
        {
            lock (_messageQueue)
            {
                QueueItem item = new QueueItem(new Message(text), callback);
                _messageQueue.Add(item);
            }
        }

        public IEnumerable<Message> GetQueuedMessages()
        {
            lock (_messageQueue)
            {
                return new List<Message>(_messageQueue.Select(x => x.Message));
            }
        }

        public Message PeekQueue()
        {
            lock (_messageQueue)
            {
                return _messageQueue[0].Message;
            }
        }

        public void ClearQueue()
        {
            lock(_messageQueue)
            {
                _messageQueue.Clear();
            }
        }

        private void PingLoop()
        {
            while (!_threadShouldDie)
            {
                if (!_disconnecting && PingEnabled)
                {
                    Send("PING"); // if the connection goes down, Send() will notify clients and start the reconnect attempt
                    Thread.Sleep(5000); // pause 5 seconds between PINGs
                }
            }
        }

        private void ProcessMessageLoop()
        {
            while (!_threadShouldDie)
            {
                // handle initial connection via listener or tcpClient
                if (Type == MessengerType.Receive && _listener != null && _listening && _listener.Pending())
                {
                    // only accept 1 client
                    if (_client == null)
                    {
                        OnConnecting?.Invoke(this, new EventArgs());
                        _client = _listener.AcceptTcpClient();
                        _listening = false;
                        _connected = true;
                        _connectionAttempts = 0;
                        _listener.Stop();
                        OnConnected?.Invoke(this, new EventArgs());
                    }
                    else
                    {
                        _connectionAttempts++;
                        OnConnectionRetry?.Invoke(this, HostName);
                        Thread.Sleep(3000); // wait for connections intermittently
                    }
                }
                else if (Type == MessengerType.Transmit && _client == null)
                {
                    OnConnecting?.Invoke(this, new EventArgs());
                    TcpClient client = new TcpClient();
                    try
                    {
                        client.ConnectAsync(EndPointAddress, Port).Wait(10000);
                    }
                    catch (Exception ex)
                    {
                        // couldn't connect yet... this is fine
                    }

                    if (client.Connected)
                    {
                        _client = client;
                        _connected = true;
                        _connectionAttempts = 0;
                        OnConnected?.Invoke(this, new EventArgs());
                    }
                    else
                    {
                        _connectionAttempts++;
                        OnConnectionRetry?.Invoke(this, HostName);
                        Thread.Sleep(3000);
                    }
                }

                // get the stream once the client is connected
                if (_stream == null && _client != null && _client.Connected)
                {
                    _stream = _client.GetStream();
                }

                // see if there's anything in the queue to send, and send it
                if (_stream != null && _client.Connected)
                {
                    lock (_messageQueue)
                    {
                        if (!_disconnecting && _messageQueue.Count > 0)
                        {
                            foreach (QueueItem messageAndCallback in _messageQueue)
                            {
                                Send($"{ messageAndCallback.Message.ToString() }");
                                messageAndCallback.Callback?.Invoke($"{ messageAndCallback.Message.ToString() }");
                            }
                            _messageQueue.Clear();
                        }
                    }
                }

                // now handle any message data sent to us
                try
                {
                    if (!_disconnecting && _stream != null && _stream.DataAvailable)
                    {
                        int i;
                        while ((i = _stream.Read(_buffer, 0, _buffer.Length)) != 0)
                        {
                            string message = $"{ _data }{ System.Text.Encoding.ASCII.GetString(_buffer, 0, i) }";

                            // ignore incomplete messages until the whole message is available
                            if (message.EndsWith("\n"))
                            {
                                string cleanedMessage = message.Replace("\n", String.Empty);
                                if (cleanedMessage != String.Empty && cleanedMessage != "PING")
                                {
                                    if (cleanedMessage.StartsWith("ACK "))
                                    {
                                        OnReceiveAcknowledge?.Invoke(this, Guid.Parse(cleanedMessage.Substring(4)));
                                    }
                                    else
                                    {
                                        Message incomingMessage = Message.FromMessageString(cleanedMessage);
                                        Send($"ACK { incomingMessage.ID }");
                                        OnReceiveMessage?.Invoke(this, incomingMessage);
                                    }
                                }

                                Array.Clear(_buffer, 0, _buffer.Length); // buffer must be cleared to avoid corruption
                                _data = String.Empty;
                                break;
                            }
                            else
                            {
                                // store the current partial message for next go around the loop
                                _data = message;
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // probably the client closed down... this will get handled on the next loop
                }
                catch (Exception ex)
                {
                    throw; // anything else is fatal
                }
            }

        }

        private void Send(string text)
        {
            if (_stream != null && _client != null && _connected && _client.Connected)
            {
                try
                {
                    byte[] textBytes = Encoding.ASCII.GetBytes($"{text}\n");
                    _stream.Write(textBytes, 0, textBytes.Length);
                }
                catch
                {
                    // connection was forcibly closed - shut it down and wait for re-connection
                    _disconnecting = true;
                    _stream?.Close();
                    _stream = null;
                    _client?.Close();
                    _client = null;
                    _connected = false;
                    _disconnecting = false;
                    if (Type == MessengerType.Receive)
                    {
                        _listener.Start();
                        _listening = true; // start listening again
                    }
                    OnDisconnected?.Invoke(this, new EventArgs());
                }
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _threadShouldDie = true;
                    _stream?.Dispose();
                    _client?.Dispose();
                }

                _disposed = true;
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        public Messenger(IPAddress endPointAddress, ushort port, MessengerType type, string hostName)
        {
            HostName = hostName;
            EndPointAddress = endPointAddress;
            Port = port;
            Type = type;
        }
    }
}
