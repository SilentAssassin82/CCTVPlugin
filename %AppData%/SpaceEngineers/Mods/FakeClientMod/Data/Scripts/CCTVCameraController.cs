using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRageMath;
using System;
using System.Collections.Generic;

namespace CCTVMod
{
    /// <summary>
    /// Client-side mod that receives camera positions from Torch plugin
    /// and controls spectator camera locally (NO CHARACTER NEEDED!)
    /// Uses allowed multiplayer messaging API - no TCP/Thread restrictions!
    /// </summary>
    [MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]
    public class CCTVCameraController : MySessionComponentBase
    {
        private const ushort MESSAGE_ID = 12346; // Unique message ID for CCTV system
        
        private readonly Dictionary<string, Vector3D> _cameraPositions = new Dictionary<string, Vector3D>();
        private readonly Queue<Action> _gameThreadActions = new Queue<Action>();
        
        private bool _isInitialized = false;
        private int _tickCounter = 0;

        public override void LoadData()
        {
            // Only run on clients (not dedicated servers)
            if (MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated)
            {
                return;
            }

            try
            {
                // Register multiplayer message handler (ALLOWED by sandbox!)
                MyAPIGateway.Multiplayer.RegisterMessageHandler(MESSAGE_ID, OnMessageReceived);
                
                _isInitialized = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Receives multiplayer messages from Torch plugin.
        /// Messages include a target SteamID — only act if it matches this client.
        /// Format: "COMMAND|TargetSteamID|...params..."
        /// </summary>
        private void OnMessageReceived(byte[] data)
        {
            try
            {
                string message = System.Text.Encoding.UTF8.GetString(data);
                string[] parts = message.Split('|');

                if (parts.Length < 2)
                    return;

                string command = parts[0];

                // parts[1] is always the target SteamID — check if this message is for us
                ulong targetSteamId;
                if (!ulong.TryParse(parts[1], out targetSteamId))
                    return;

                ulong mySteamId = MyAPIGateway.Multiplayer.MyId;
                if (targetSteamId != mySteamId)
                    return; // Not for us — ignore

                switch (command)
                {
                    case "INDEX":
                        // INDEX|SteamID|CameraName|X|Y|Z
                        if (parts.Length >= 6)
                        {
                            string cameraName = parts[2];
                            double x = double.Parse(parts[3]);
                            double y = double.Parse(parts[4]);
                            double z = double.Parse(parts[5]);

                            _cameraPositions[cameraName] = new Vector3D(x, y, z);
                        }
                        break;

                    case "INDEX_COMPLETE":
                        break;

                    case "GOTO":
                        // GOTO|SteamID|CameraName|EntityID|X|Y|Z|FwdX|FwdY|FwdZ|UpX|UpY|UpZ
                        if (parts.Length >= 8)
                        {
                            string cameraName = parts[2];
                            long entityId = long.Parse(parts[3]);
                            Vector3D position = new Vector3D(
                                double.Parse(parts[4]),
                                double.Parse(parts[5]),
                                double.Parse(parts[6])
                            );

                            lock (_gameThreadActions)
                            {
                                _gameThreadActions.Enqueue(() => MoveToCamera(cameraName, entityId, position));
                            }
                        }
                        break;
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Switches the local camera to view through a camera block entity.
        /// Uses SetCameraController(Entity) for correct orientation.
        /// Falls back to spectator mode if entity not found on client.
        /// </summary>
        private void MoveToCamera(string cameraName, long entityId, Vector3D fallbackPosition)
        {
            try
            {
                // Try to find the camera entity on the client
                VRage.ModAPI.IMyEntity entity = MyAPIGateway.Entities.GetEntityById(entityId);

                if (entity != null)
                {
                    // View through the camera block — gives correct position AND orientation
                    MyAPIGateway.Session.SetCameraController(
                        MyCameraControllerEnum.Entity,
                        entity
                    );
                }
                else
                {
                    // Entity not loaded on client — fall back to spectator at position
                    MyAPIGateway.Session.SetCameraController(
                        MyCameraControllerEnum.Spectator,
                        null,
                        fallbackPosition
                    );
                }
            }
            catch (Exception)
            {
            }
        }

        public override void UpdateAfterSimulation()
        {
            if (!_isInitialized)
                return;

            // Execute queued actions on game thread
            lock (_gameThreadActions)
            {
                while (_gameThreadActions.Count > 0)
                {
                    var action = _gameThreadActions.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            // Periodic heartbeat
            _tickCounter++;
            if (_tickCounter >= 600) // Every 10 seconds
            {
                _tickCounter = 0;
                // Could send status updates here if needed
            }
        }

        protected override void UnloadData()
        {
            if (_isInitialized)
            {
                try
                {
                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(MESSAGE_ID, OnMessageReceived);
                }
                catch { }
            }
        }
    }
}
