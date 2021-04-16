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
        public const string INTERNAL_MESSAGE_PREFIX = "$$";
        public const string PING_MESSAGE = INTERNAL_MESSAGE_PREFIX + "PING";
        public const string ACK_MESSAGE = INTERNAL_MESSAGE_PREFIX + "ACK";

        private Task _processMessagesTask;
        private Task _pingTask;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;

        private bool _disposed;
        private bool _connected;
        private bool _disconnecting;
        private bool _listening;
        private bool _terminateThreads;

        private byte[] _buffer = new byte[32768];
        private string _data = String.Empty;

        private int _connectionAttempts;

        private List<QueueEntry> _messageQueue = new List<QueueEntry>();

        public IPAddress EndPointAddress { get; private set; }
        public string HostName { get; private set; }
        public ushort Port { get; private set; }
        public MessengerType Type { get; private set; }
        public bool Connected => _connected;
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
                    _processMessagesTask = Task.Run(MessageLoop);
                    _pingTask = Task.Run(PingLoop);
                }
            }
        }

        public void Close()
        {
            _terminateThreads = true;
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

        public void QueueMessage(string text, Action<string> callbackAfterSent = null)
        {
            lock (_messageQueue)
            {
                if (text.StartsWith(INTERNAL_MESSAGE_PREFIX))
                {
                    throw new ArgumentException($"Message begins with illegal sequence '{ INTERNAL_MESSAGE_PREFIX }' - use '\\{ INTERNAL_MESSAGE_PREFIX }' to escape.");
                }

                QueueEntry item = new QueueEntry(new Message(text), callbackAfterSent);
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
            while (!_terminateThreads)
            {
                if (!_disconnecting && PingEnabled)
                {
                    Send(PING_MESSAGE); // if the connection goes down, Send() will notify clients and start the reconnect attempt
                    Thread.Sleep(5000); // pause 5 seconds between PINGs
                }
            }
        }

        private void MessageLoop()
        {
            while (!_terminateThreads)
            {
                // handle initial connection via listener or tcpClient
                if (Type == MessengerType.Receive && _listener != null && _listening && _listener.Pending())
                {
                    // only accept 1 client
                    if (_client == null)
                    {
                        OnConnecting?.Invoke(this, EventArgs.Empty);
                        _client = _listener.AcceptTcpClient();
                        _listener.Stop();
                        _listening = false;
                        _connected = true;
                        OnConnected?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        OnConnectionRetry?.Invoke(this, HostName);
                        Thread.Sleep(3000); // wait for connections intermittently
                    }
                }
                else if (Type == MessengerType.Transmit && _client == null)
                {
                    OnConnecting?.Invoke(this, EventArgs.Empty);
                    TcpClient client = new TcpClient();
                    try
                    {
                        client.ConnectAsync(EndPointAddress, Port).Wait(10000);
                    }
                    catch
                    {
                        // couldn't connect yet... this is fine
                    }

                    if (client.Connected)
                    {
                        _client = client;
                        _connected = true;
                        OnConnected?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
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
                            foreach (QueueEntry messageAndCallback in _messageQueue)
                            {
                                if (Send($"{ messageAndCallback.Message.ToString() }"))
                                {
                                    _messageQueue.Remove(messageAndCallback);
                                }

                                messageAndCallback.Callback?.Invoke($"{ messageAndCallback.Message.ToString() }");
                            }
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
                                if (cleanedMessage != String.Empty && cleanedMessage != PING_MESSAGE)
                                {
                                    if (cleanedMessage.StartsWith(ACK_MESSAGE))
                                    {
                                        OnReceiveAcknowledge?.Invoke(this, Guid.Parse(cleanedMessage.Substring(4)));
                                    }
                                    else
                                    {
                                        Message incomingMessage = Message.Parse(cleanedMessage);
                                        Send($"{ ACK_MESSAGE } { incomingMessage.ID }");
                                        OnReceiveMessage?.Invoke(this, incomingMessage);
                                    }
                                }

                                Array.Clear(_buffer, 0, _buffer.Length); // buffer must be cleared to avoid corruption
                                _data = String.Empty;
                                break;
                            }
                            else
                            {
                                // store the current partial message for next time around the loop
                                _data = message;
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // probably the client closed down... this will get handled in the next loop
                }
                catch
                {
                    throw; // anything else is fatal
                }
            }

        }

        private bool Send(string text)
        {
            if (_stream != null && _client != null && _connected && _client.Connected)
            {
                try
                {
                    if (text.StartsWith($"\\{ INTERNAL_MESSAGE_PREFIX }")) text = text.Substring(1); // handle escaped prefix
                    byte[] textBytes = Encoding.ASCII.GetBytes($"{text}\n");
                    _stream.Write(textBytes, 0, textBytes.Length);
                    return true;
                }
                catch
                {
                    // connection was forcibly closed - shut it down and wait for re-connection
                    OnDisconnected?.Invoke(this, EventArgs.Empty);
                    Close();
                    Open();
                    return false;
                }
            }

            return false;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _terminateThreads = true;
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
