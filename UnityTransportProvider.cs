using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;
using Netick;
using Netick.Unity;
using static NetickUnityTransport;

[CreateAssetMenu(fileName = "UnityTransportProvider", menuName = "Netick/Transport/UnityTransportProvider", order = 1)]
public class        UnityTransportProvider : NetworkTransportProvider { public override NetworkTransport           MakeTransportInstance()                                => new NetickUnityTransport(); }
public static class NetickUnityTransportExt                           { public static NetickUnityTransportEndPoint ToNetickEndPoint(this NetworkEndpoint networkEndpoint) => new NetickUnityTransportEndPoint(networkEndpoint); }

public unsafe class NetickUnityTransport : NetworkTransport
{
  public struct NetickUnityTransportEndPoint : IEndPoint
  {
    public NetworkEndpoint EndPoint;
    string       IEndPoint.IPAddress => EndPoint.Address.ToString();
    int          IEndPoint.Port      => EndPoint.Port;
    public NetickUnityTransportEndPoint(NetworkEndpoint networkEndpoint)
    {
      EndPoint = networkEndpoint;
    }
    public override string ToString()
    {
      return $"{EndPoint.Address}";
    }
  }

  public unsafe class NetickUnityTransportConnection : TransportConnection
  {
    public NetickUnityTransport                         Transport;
    public Unity.Networking.Transport.NetworkConnection Connection;
    public override IEndPoint                           EndPoint => Transport._driver.GetRemoteEndpoint(Connection).ToNetickEndPoint();
    public override int                                 Mtu      => MaxPayloadSize;

    public int                                          MaxPayloadSize;

    public NetickUnityTransportConnection(NetickUnityTransport transport)
    {
      Transport = transport;
    }

    public unsafe override void Send(IntPtr ptr, int length)
    {
      if (!Connection.IsCreated)
        return;
      Transport._driver.BeginSend(NetworkPipeline.Null, Connection, out var networkWriter);
      networkWriter.    WriteBytesUnsafe((byte*)ptr.ToPointer(), length);
      Transport._driver.EndSend(networkWriter);
    }
  }

  private NetworkDriver                                                                            _driver;
  private Dictionary<Unity.Networking.Transport.NetworkConnection, NetickUnityTransportConnection> _connectedPeers        = new();
  private Queue<NetickUnityTransportConnection>                                                    _freeConnections       = new();
  private Unity.Networking.Transport.NetworkConnection                                             _serverConnection;

  private NativeList<Unity.Networking.Transport.NetworkConnection>                                 _connections;

  private BitBuffer                                                                                _bitBuffer; 
  private byte*                                                                                    _bytesBuffer;
  private int                                                                                      _bytesBufferSize       = 2048;
  private byte[]                                                                                   _connectionRequestBytes       = new byte[200];
  private NativeArray<byte>                                                                        _connectionRequestNative = new NativeArray<byte>(200, Allocator.Persistent);

  public NetickUnityTransport()
  {
    _bytesBuffer = (byte*)UnsafeUtility.Malloc(_bytesBufferSize, 4, Unity.Collections.Allocator.Persistent);
  }

  ~NetickUnityTransport()
  {
    UnsafeUtility.Free(_bytesBuffer, Unity.Collections.Allocator.Persistent);
    _connectionRequestNative.Dispose();
  }

  public override void Init()
  {
    _bitBuffer      = new BitBuffer(createChunks: false);
    _driver      = NetworkDriver.Create(new WebSocketNetworkInterface());
    _connections = new NativeList<Unity.Networking.Transport.NetworkConnection>(Engine.IsServer ? Engine.Config.MaxPlayers : 0, Unity.Collections.Allocator.Persistent);
  }

  public override void Run(RunMode mode, int port)
  {
    if (Engine.IsServer)
    {
      var endpoint = NetworkEndpoint.AnyIpv4.WithPort((ushort)port);

      if (_driver.Bind(endpoint) != 0)
      {
        Debug.LogError($"Failed to bind to port {port}");
        return;
      } 
      _driver.Listen();
    }

    for (int i = 0; i < Engine.Config.MaxPlayers; i++)
      _freeConnections.Enqueue(new NetickUnityTransportConnection(this));
  }

  public override void Shutdown()
  {
    if (_driver.IsCreated)
      _driver.   Dispose();    
    _connections.Dispose();
  }

  public override void Connect(string address, int port, byte[] connectionData, int connectionDataLength)
  {
      var endpoint      = NetworkEndpoint.Parse(address, (ushort)port); 
    if (connectionData != null)
    {
      _connectionRequestNative.CopyFrom(connectionData);
      _serverConnection = _driver.Connect(endpoint, _connectionRequestNative);
    }
    else
      _serverConnection = _driver.Connect(endpoint);
  }

  public override void Disconnect(TransportConnection connection)
  {
    var conn = (NetickUnityTransport.NetickUnityTransportConnection)connection;
    if (conn.Connection.IsCreated)
      _driver.Disconnect(conn.Connection);
  }

  public override void PollEvents()
  {
    _driver.ScheduleUpdate().Complete();

    if (Engine.IsClient && !_serverConnection.IsCreated)
      return;

    // reading events
    if (Engine.IsServer)
    {
      // clean up connections.
      for (int i = 0; i < _connections.Length; i++)
      {
        if (!_connections[i].IsCreated)
        {
          _connections.RemoveAtSwapBack(i);
          i--;
        }
      }

      // accept new connections in the server.
      Unity.Networking.Transport.NetworkConnection c;
      while ((c = _driver.Accept(out var payload )) != default)
      {
        if (_connectedPeers.Count >= Engine.Config.MaxPlayers)
        {
          _driver.Disconnect(c);
          continue;
        }

        if (payload.IsCreated)
          payload.CopyTo(_connectionRequestBytes);
        bool accepted = NetworkPeer.OnConnectRequest(_connectionRequestBytes, payload.Length, _driver.GetRemoteEndpoint(c).ToNetickEndPoint());

        if (!accepted)
        {
          _driver.Disconnect(c);
          continue;
        }

        var connection        = _freeConnections.Dequeue();
        connection.Connection = c;
        _connectedPeers.Add(c, connection);
        _connections.   Add(c);

        connection.MaxPayloadSize = NetworkParameterConstants.MTU - _driver.MaxHeaderSize(NetworkPipeline.Null);
        NetworkPeer.    OnConnected(connection);
      }

      for (int i = 0; i < _connections.Length; i++)
        HandleConnectionEvents(_connections[i], i);
    }
    else
      HandleConnectionEvents(_serverConnection, 0);
  }


  private void HandleConnectionEvents(Unity.Networking.Transport.NetworkConnection conn, int index)
  {
    DataStreamReader  stream;
    NetworkEvent.Type cmd;

    while ((cmd = _driver.PopEventForConnection(conn, out stream)) != NetworkEvent.Type.Empty)
    {
      // game data
      if (cmd == NetworkEvent.Type.Data)
      {
        if (_connectedPeers.TryGetValue(conn, out var netickConn))
        {
          stream.     ReadBytesUnsafe(_bytesBuffer, stream.Length);
          _bitBuffer.    SetFrom(_bytesBuffer, stream.Length, _bytesBufferSize);
          NetworkPeer.Receive(netickConn, _bitBuffer);
        }
      }

      // connected to server
      if (cmd == NetworkEvent.Type.Connect && Engine.IsClient)
      {
        var connection = _freeConnections.Dequeue();
        connection.Connection = conn;

        _connectedPeers.Add(conn, connection);
        _connections.   Add(conn);

        connection.MaxPayloadSize = NetworkParameterConstants.MTU - _driver.MaxHeaderSize(NetworkPipeline.Null);
        NetworkPeer.    OnConnected(connection);
      }

      // disconnect
      if (cmd == NetworkEvent.Type.Disconnect)
      {
        if (_connectedPeers.TryGetValue(conn, out var netickConn))
        {
          TransportDisconnectReason reason = TransportDisconnectReason.Shutdown;

          NetworkPeer.     OnDisconnected(netickConn, reason);
          _freeConnections.Enqueue(netickConn);
          _connectedPeers. Remove(conn);
        }

        if (Engine.IsClient)
          _serverConnection   = default;
        if (Engine.IsServer)
          _connections[index] = default;
      }
    }
  }
}
