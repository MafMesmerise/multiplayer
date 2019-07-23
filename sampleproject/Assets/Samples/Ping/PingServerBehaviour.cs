﻿using Unity.Burst;
using UnityEngine;
using Unity.Networking.Transport;
using Unity.Collections;
using Unity.Jobs;

public class PingServerBehaviour : MonoBehaviour
{
    public UdpNetworkDriver m_ServerDriver;
    private NativeList<NetworkConnection> m_connections;

    private JobHandle m_updateHandle;

    private NativeArray<int> output;
    private string status;

    void Start()
    {
        ushort serverPort = 9000;
        ushort newPort = 0;
        if (CommandLine.TryGetCommandLineArgValue("-port", out newPort))
            serverPort = newPort;
        // Create the server driver, bind it to a port and start listening for incoming connections
        m_ServerDriver = new UdpNetworkDriver(new INetworkParameter[0]);
        var addr = NetworkEndPoint.AnyIpv4;
        addr.Port = serverPort;
        if (m_ServerDriver.Bind(addr) != 0)
            Debug.Log($"Failed to bind to port {serverPort}");
        else
            m_ServerDriver.Listen();

        m_connections = new NativeList<NetworkConnection>(16, Allocator.Persistent);
        output = new NativeArray<int>(1, Allocator.Persistent);
    }
    /*
    void OnGUI()
    {
        if (m_connections.IsCreated)
        {
            GUILayout.Label("Connections: " + m_connections.Length);
            for (int i = 0; i < m_connections.Length; i++)
            {
                if (m_connections[i].IsCreated)
                {
                    NetworkConnection connection = m_connections[i];
                    GUILayout.Label("Connection " + i + " state: " + connection.GetState<UdpNetworkDriver>(m_ServerDriver).ToString());
                    //DataStreamReader dsr = new DataStreamReader();
                    //connection.PopEvent<UdpNetworkDriver>(m_ServerDriver, out dsr);
                    //GUILayout.Label(dsr.Length.ToString());
                }
                else
                    GUILayout.Label("Connection " + i + " NOT CREATED");
            }
        }
    }
    */
    void OnDestroy()
    {
        // All jobs must be completed before we can dispose the data they use
        m_updateHandle.Complete();
        m_ServerDriver.Dispose();
        m_connections.Dispose();
        output.Dispose();
    }

    [BurstCompile]
    struct DriverUpdateJob : IJob
    {
        public UdpNetworkDriver driver;
        public NativeList<NetworkConnection> connections;
        
        public void Execute()
        {
            // Remove connections which have been destroyed from the list of active connections
            for (int i = 0; i < connections.Length; ++i)
            {
                if (!connections[i].IsCreated)
                {
                    connections.RemoveAtSwapBack(i);
                    // Index i is a new connection since we did a swap back, check it again
                    --i;
                }
            }

            // Accept all new connections
            while (true)
            {
                var con = driver.Accept();
                // "Nothing more to accept" is signaled by returning an invalid connection from accept
                if (!con.IsCreated)
                    break;
                connections.Add(con);
            }
        }
    }

    static NetworkConnection ProcessSingleConnection(UdpNetworkDriver.Concurrent driver, NetworkConnection connection, out int feedback)
    {
        DataStreamReader strm;
        NetworkEvent.Type cmd;

        feedback = -1;

        // Pop all events for the connection
        while ((cmd = driver.PopEventForConnection(connection, out strm)) != NetworkEvent.Type.Empty)
        {
            if (cmd == NetworkEvent.Type.Data)
            {
                // For ping requests we reply with a pong message. A DataStreamReader.Context is required to keep track of current read position since DataStreamReader is immutable
                DataStreamReader.Context readerCtx = default(DataStreamReader.Context);
                int id = strm.ReadInt(ref readerCtx);
                feedback = id;
                // Create a temporary DataStreamWriter to keep our serialized pong message
                DataStreamWriter pongData = new DataStreamWriter(4, Allocator.Temp);
                pongData.Write(id);
                // Send the pong message with the same id as the ping
                driver.Send(NetworkPipeline.Null, connection, pongData);
            }
            else if (cmd == NetworkEvent.Type.Disconnect)
            {                
                // When disconnected we make sure the connection return false to IsCreated so the next frames DriverUpdateJob will remove it                
                return default(NetworkConnection);
            }
        }

        return connection;
    }
#if ENABLE_IL2CPP
    [BurstCompile]
    struct PongJob : IJob
    {
        public UdpNetworkDriver.Concurrent driver;
        public NativeList<NetworkConnection> connections;

        public void Execute()
        {
            for (int i = 0; i < connections.Length; ++i)
                connections[i] = ProcessSingleConnection(driver, connections[i]);
        }
    }
#else
    [BurstCompile]
    struct PongJob : IJobParallelForDefer
    {
        public UdpNetworkDriver.Concurrent driver;
        public NativeArray<NetworkConnection> connections;
        public NativeArray<int> feedback;

        public void Execute(int i)
        {
            int output = -1;
            connections[i] = ProcessSingleConnection(driver, connections[i], out output);
            if (output != -1)
                feedback[0] = output;
        }
    }
#endif

    void LateUpdate()
    {
        // On fast clients we can get more than 4 frames per fixed update, this call prevents warnings about TempJob
        // allocation longer than 4 frames in those cases
        m_updateHandle.Complete();
    }

    void UpdateStatus()
    {
        if (m_connections.IsCreated && m_connections.Length > 0 && output[0] != -1)
        {
            status = "";
            status += "Connections: " + m_connections.Length;
            for (int i = 0; i < m_connections.Length; i++)
            {
                if (m_connections[i].IsCreated)
                {
                    NetworkConnection connection = m_connections[i];
                    status += "\nConnection " + i + " state: " + connection.GetState<UdpNetworkDriver>(m_ServerDriver).ToString();
                    status += "\nData read: " + output[0];
                    /*
                    DataStreamReader dsr = new DataStreamReader();
                    connection.PopEvent<UdpNetworkDriver>(m_ServerDriver, out dsr);
                    if (dsr.IsCreated)
                    {
                        status += " - connection PopEvent data iscreated " + dsr.IsCreated;
                        if (dsr.Length > 0)
                            status += "connection PopEvent data length " + dsr.Length;
                    }
                    else
                    {
                        status += " - connection PopEvent data IS NOT CREATED";
                    }*/
                }
                // else                    status += "\nConnection " + i + " NOT isCreated";
            }
        }
        else if (status == "")
            status = "No valid connections";


        ServerStatusUI.instance.UpdateStatus(status);

        /*
        using (var dataWriter = new DataStreamWriter(16, Allocator.Persistent))
        {
            dataWriter.Write(42);
            dataWriter.Write(1234);
            // Length is the actual amount of data inside the writer,
            // Capacity is the total amount.
            var dataReader = new DataStreamReader(dataWriter, 0, dataWriter.Length);
            var context = default(DataStreamReader.Context);
            var myFirstInt = dataReader.ReadInt(ref context);
            var mySecondInt = dataReader.ReadInt(ref context);
        }*/
    }

    void FixedUpdate()
    {
        // Wait for the previous frames ping to complete before starting a new one, the Complete in LateUpdate is not
        // enough since we can get multiple FixedUpdate per frame on slow clients
        m_updateHandle.Complete();

        // If there is at least one client connected update the activity so the server is not shutdown
        if (m_connections.Length > 0)
            DedicatedServerConfig.UpdateLastActivity();


        if (ServerStatusUI.instance)            UpdateStatus();


        var updateJob = new DriverUpdateJob {driver = m_ServerDriver, connections = m_connections};

        var pongJob = new PongJob
        {
            // PongJob is a ParallelFor job, it must use the concurrent NetworkDriver
            driver = m_ServerDriver.ToConcurrent(),
            // PongJob uses IJobParallelForDeferExtensions, we *must* use AsDeferredJobArray in order to access the
            // list from the job
#if ENABLE_IL2CPP
            // IJobParallelForDeferExtensions is not working correctly with IL2CPP
            connections = m_connections,
#else
            connections = m_connections.AsDeferredJobArray(),
#endif
            feedback = output
        };
        // Update the driver should be the first job in the chain
        m_updateHandle = m_ServerDriver.ScheduleUpdate();
        // The DriverUpdateJob which accepts new connections should be the second job in the chain, it needs to depend on the driver update job
        m_updateHandle = updateJob.Schedule(m_updateHandle);
        // PongJob uses IJobParallelForDeferExtensions, we *must* schedule with a list as first parameter rather than an int since the job needs to pick up new connections from DriverUpdateJob
        // The PongJob is the last job in the chain and it must depends on the DriverUpdateJob
#if ENABLE_IL2CPP
        m_updateHandle = pongJob.Schedule(m_updateHandle);
#else
        m_updateHandle = pongJob.Schedule(m_connections, 1, m_updateHandle);
#endif

    }
}
