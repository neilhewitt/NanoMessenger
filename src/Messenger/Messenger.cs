using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public static Messenger Transmitter(string nickname, string remoteHost, ushort port, int pingTimeoutInSeconds = 5)
        {
            return new Messenger(nickname, remoteHost, port, pingTimeoutInSeconds);
        }

        public static Messenger Receiver(string nickname, ushort port, int pingTimeoutInSeconds = 5)
        {
            return new Messenger(nickname, port, pingTimeoutInSeconds);
        }

        public const string INTERNAL_MESSAGE_PREFIX = "$$";
        public const string PING_MESSAGE = INTERNAL_MESSAGE_PREFIX + "PING";
        public const string PING_BACK_MESSAGE = INTERNAL_MESSAGE_PREFIX + "PINGBACK";
        public const string ACK_MESSAGE = INTERNAL_MESSAGE_PREFIX + "ACK ";
        public const string END_OF_MESSAGE = INTERNAL_MESSAGE_PREFIX + "ENDS";

        public const int BUFFER_SIZE = 65536;

        private Thread _processMessagesTask;
        private Thread _pingTask;

        private TcpListener _listener;
        private TcpClient _client;
        private NetworkStream _stream;

        private bool _disposed;
        private bool _connected;
        private bool _disconnecting;
        private bool _listening;
        private bool _terminateThreads;
        private bool _canPing;
        private bool _pingBackPending;
        private bool _pingBackReceived;

        private int _pingTimeoutInSeconds;

        private byte[] _buffer = new byte[BUFFER_SIZE];
        private string _data = String.Empty;

        private List<QueueEntry> _messageQueue = new List<QueueEntry>();

        public string Name { get; private set; }
        public IPAddress RemoteAddress { get; private set; }
        public string RemoteHostName { get; private set; }
        public string LocalHostName { get; private set; }
        public IPAddress LocalAddress { get; private set; }
        public ushort Port { get; private set; }
        public MessengerType Type { get; private set; }
        public bool Connected => _connected;
        public bool PingEnabled { get; set; } = false;
        public int QueueLength => _messageQueue?.Count ?? -1;

        public event EventHandler<Message> OnReceiveMessage;
        public event EventHandler<Guid> OnReceiveAcknowledge;
        public event EventHandler OnConnecting;
        public event EventHandler<string> OnConnectionRetry;
        public event EventHandler OnConnected;
        public event EventHandler OnDisconnected;
        public event EventHandler<string> OnPing;
        public event EventHandler<string> OnPingBack;

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

                if ((_processMessagesTask == null && _pingTask == null))
                {
                    _processMessagesTask = new Thread(MessageLoop);
                    _pingTask = new Thread(PingLoop);

                    _terminateThreads = false;

                    _processMessagesTask.Start();
                    _pingTask.Start();
                }
            }
        }

        public void Close()
        {
            _canPing = false;
            _disconnecting = true;
            _connected = false;

            _terminateThreads = true;
            _processMessagesTask = null;
            _pingTask = null;

            _listener?.Stop();
            _listening = false;
            _listener = null;

            _stream?.Close();
            _client?.Close();
            _client = null;
            _stream = null;

            _disconnecting = false;
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
                if (PingEnabled && RemoteAddress != null && _connected && _canPing)
                {
                    if (!_pingBackPending)
                    {
                        OnPing?.Invoke(this, RemoteHostName);
                        _pingBackPending = true;
                        _pingBackReceived = false;
                        Send(PING_MESSAGE); // PING should be responded to with PINGBACK or else the connection is down
                        AwaitPingBack();
                        Thread.Sleep(3000);
                    }
                }

                Thread.Sleep(1);
            }
        }

        private void AwaitPingBack()
        {
            DateTime start = DateTime.Now;
            
            // there must be a more attractive way of doing this... wait until the pingback happens or timeout occurs
            while (!_pingBackReceived && DateTime.Now.Subtract(start).Seconds < _pingTimeoutInSeconds) ;
            
            if (!_pingBackReceived)
            {
                OnDisconnected?.Invoke(this, EventArgs.Empty);
                Close();
                Open();
            }

            _pingBackPending = false;
        }

        private void MessageLoop()
        {
            while (!_terminateThreads)
            {
                if (_client == null)
                {
                    ConnectIfReceiver();
                    ConnectIfTransmitter();
                }
                else
                {
                    try
                    {
                        // generally there will only be one incoming message per go around the loop, but if the thread
                        // runs too slowly then multiple messages may be in the stream unread, so this method
                        // will handle multiple messages if necessary
                        ReceiveIncomingMessages();
                    }
                    catch (IOException)
                    {
                        // probably the client closed down... let's start trying to reconnect
                        Close();
                        Open();
                    }
                    catch
                    {
                        throw; // anything else is fatal
                    }

                    // messages are pulled from a queue, but the queue is really only there to provide resilience from disconnection;
                    // only the top-most message is sent now, so the queue is now effectively a buffer; sending all messages at once
                    // is faster but you risk overloading the receive buffer on the other end
                    SendOutgoingMessage();
                }

                Thread.Sleep(1);
            }
        }

        private void ConnectIfReceiver()
        {
            if (!_disconnecting && Type == MessengerType.Receive && _listener != null && _listening && _listener.Pending())
            {
                OnConnecting?.Invoke(this, EventArgs.Empty);
                
                _client = _listener.AcceptTcpClient();
                RemoteAddress = ((IPEndPoint)_client.Client.RemoteEndPoint).Address;
                RemoteHostName = Dns.GetHostEntry(RemoteAddress).HostName;

                _listener.Stop();
                _listening = false;
                _listener = null;
                _connected = true;

                OnConnected?.Invoke(this, EventArgs.Empty);

                GetStream();
                _canPing = true;
            }
        }

        private void ConnectIfTransmitter()
        {
            if (!_disconnecting && Type == MessengerType.Transmit)
            {
                OnConnecting?.Invoke(this, EventArgs.Empty);
                TcpClient client = new TcpClient();
                try
                {
                    client.Connect(RemoteAddress, Port);
                }
                catch (Exception ex)
                {
                    // couldn't connect yet... no problem, next time round we'll try again
                }

                if (!_disconnecting && client.Connected)
                {
                    _client = client;
                    _connected = true;
                    OnConnected?.Invoke(this, EventArgs.Empty);

                    GetStream();
                    _canPing = true;
                }
                else
                {
                    OnConnectionRetry?.Invoke(this, RemoteHostName);
                    Thread.Sleep(1000);
                }
            }
        }

        private void GetStream()
        {
            if (_stream == null && _client != null && _client.Connected)
            {
                _stream = _client.GetStream();
            }
        }

        private void SendOutgoingMessage()
        {
            if (_stream != null && _client.Connected)
            {
                lock (_messageQueue)
                {
                    if (!_disconnecting && _messageQueue.Count > 0)
                    {
                        QueueEntry topOfQueue = _messageQueue.First();
                        bool sent = Send($"{ topOfQueue.Message.ToString() }");
                        if (sent) _messageQueue.RemoveAt(0);
                    }
                }
            }
        }

        private void ReceiveIncomingMessages()
        {
            if (!_disconnecting && _stream != null && _stream.DataAvailable)
            {
                int i;
                while ((i = _stream.Read(_buffer, 0, _buffer.Length)) != 0)
                {
                    string data = $"{ _data }{ System.Text.Encoding.ASCII.GetString(_buffer, 0, i) }";
                    Array.Clear(_buffer, 0, _buffer.Length);

                    // max data in the read buffer is BUFFER_SIZE bytes which should be enough for most messages
                    // but in case it isn't, messages will be chunked; each message-end is noted with an escape sequence
                    // and if the data just read from the stream doesn't end with the end-of-message token, then
                    // we'll stuff the data into a string field and concatenate the next set of data from the stream until
                    // we have a complete set of messages available to process

                    // this does mean that messages may be delayed processing while we wait, but they will all be handled 
                    // eventually

                    if (data.EndsWith(END_OF_MESSAGE))
                    {
                        // if the buffer is being filled quickly we may have more than one message pending, so we'll handle them all now
                        string[] messages = data.Split(new string[] { END_OF_MESSAGE }, StringSplitOptions.RemoveEmptyEntries);

                        foreach (string message in messages)
                        {
                            if (message == PING_MESSAGE)
                            {
                                // respond to the incoming ping with a pingback
                                Send($"{ PING_BACK_MESSAGE }");
                            }
                            else if (message == PING_BACK_MESSAGE)
                            {
                                _pingBackReceived = true; // this is a semaphore for the EnsurePingBack method above
                                OnPingBack?.Invoke(this, RemoteHostName);
                            }
                            else
                            {
                                if (message.StartsWith(ACK_MESSAGE))
                                {
                                    // clients can subscribe to OnReceiveAcknowledge to know when their messages
                                    // got there - this is the limit of any auditing we do here
                                    OnReceiveAcknowledge?.Invoke(this, Guid.Parse(message.Substring(ACK_MESSAGE.Length)));
                                }
                                else
                                {
                                    Message incomingMessage = Message.Parse(message);

                                    // all messages received are acknowledged back to the client
                                    // in case the client needs to know when it's been delivered
                                    Send($"{ ACK_MESSAGE }{ incomingMessage.ID }");

                                    OnReceiveMessage?.Invoke(this, incomingMessage);
                                }
                            }
                        }

                        _data = String.Empty;
                        break;
                    }
                    else
                    {
                        // store the current partial message for next time around the loop
                        _data = data;
                    }
                }
            }
        }

        private bool Send(string text)
        {
            if (_stream != null && _client != null && _connected && _client.Connected)
            {
                try
                {
                    byte[] textBytes = Encoding.ASCII.GetBytes($"{text}{ END_OF_MESSAGE }");
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
                    Close();
                }

                _disposed = true;
            }
        }

        void IDisposable.Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private Messenger(string name, ushort port, int pingTimeOutInSeconds = 5) : this(name, null, port, pingTimeOutInSeconds)
        {
            Type = MessengerType.Receive;
        }

        private Messenger(string name, string remoteHost, ushort port, int pingTimeOutInSeconds = 5)
        {
            Name = name;

            LocalHostName = Dns.GetHostName();
            LocalAddress = Dns.GetHostAddresses(LocalHostName)[0];
            RemoteHostName = remoteHost;
            RemoteAddress = remoteHost == null ? null : Dns.GetHostAddresses(remoteHost)[0];
            Port = port;
            Type = MessengerType.Transmit;
            _pingTimeoutInSeconds = pingTimeOutInSeconds;
        }
    }
} 
