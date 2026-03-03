using Sandbox.ModAPI;
using Sandbox.ModAPI.Interfaces.Terminal;
using VRage.Game;
using VRage.Game.Components;
using VRageMath;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

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
        private const ushort MESSAGE_ID = 12346; // server → client: GOTO / spectator control
        private const ushort CTRL_MESSAGE_ID = 12347; // client → server: button control
        private const long   CTRL_MOD_CHANNEL = 123461234L; // server-side mod → Torch plugin (same process)
        
        private readonly Dictionary<string, Vector3D> _cameraPositions = new Dictionary<string, Vector3D>();
        private readonly Queue<Action> _gameThreadActions = new Queue<Action>();
        
        // Static flag: terminal actions must only be registered once per game session.
        // Using static prevents double-registration if the session component is reloaded.
        private static bool _actionsRegistered = false;

        // Action instances held statically so they outlive any single world load.
        // Injected via CustomActionGetter (not AddAction) to avoid corrupting the
        // IMyButtonPanel terminal control list, which would remove the Settings panel
        // and CustomData field from every button panel's terminal UI.
        private static IMyTerminalAction _nextAction;
        private static IMyTerminalAction _prevAction;
        private static IMyTerminalAction _resetAction;
        private static IMyTerminalAction _nextLoopAction;
        private static IMyTerminalAction _prevLoopAction;

        private bool _isInitialized = false;
        private bool _pendingActionRegistration = false;
        private int _tickCounter = 0;

        public override void LoadData()
        {
            try
            {
                bool isDedicatedServer = MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated;

                if (!isDedicatedServer)
                {
                    // GOTO messages move the spectator camera — only meaningful on clients.
                    MyAPIGateway.Multiplayer.RegisterMessageHandler(MESSAGE_ID, OnMessageReceived);
                }

                // Defer action registration to first UpdateAfterSimulation tick.
                // LoadData() fires before SE's terminal system is ready — registering here
                // corrupts the IMyButtonPanel terminal UI for ALL button panels.
                // Must run on BOTH client and dedicated server: button presses execute
                // server-side, so the server must have the actions in its registry.
                _pendingActionRegistration = true;

                _isInitialized = true;
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Builds three terminal actions and injects them via CustomActionGetter so they
        /// appear in the button panel action picker without touching the panel's terminal controls.
        /// Called on the first UpdateAfterSimulation tick so the terminal system is ready.
        /// </summary>
        private void RegisterCameraControlActions()
        {
            if (_actionsRegistered)
                return;
            _actionsRegistered = true;

            try
            {
                _nextAction  = BuildCamCtrlAction("CCTV_Next",     "CCTV: Next Camera",  "NEXT");
                _prevAction  = BuildCamCtrlAction("CCTV_Prev",     "CCTV: Prev Camera",  "PREV");
                _resetAction = BuildCamCtrlAction("CCTV_Reset",    "CCTV: Reset Cycle",  "RESET");
                _nextLoopAction = BuildCamCtrlAction("CCTV_NextLoop", "CCTV: Next Loop",  "NEXTLOOP");
                _prevLoopAction = BuildCamCtrlAction("CCTV_PrevLoop", "CCTV: Prev Loop",  "PREVLOOP");

                // Subscribe via CustomActionGetter instead of calling AddAction.
                // AddAction permanently modifies SE's terminal registry and triggers a
                // control-list rebuild that drops the vanilla Settings panel and
                // CustomData field from every IMyButtonPanel in the world.
                // CustomActionGetter injects actions dynamically at picker-open time
                // and never touches the registry, so vanilla controls stay intact.
                MyAPIGateway.TerminalControls.CustomActionGetter += OnCustomActionGetter;
            }
            catch (Exception) { }
        }

        private static IMyTerminalAction BuildCamCtrlAction(string id, string label, string cmd)
        {
            var action = MyAPIGateway.TerminalControls.CreateAction<IMyButtonPanel>(id);
            action.Name = new StringBuilder(label);
            action.ValidForGroups = false;
            action.Enabled = (block) => true;
            action.Action = (block) =>
            {
                string lcdName = (block.CustomData ?? "").Trim();
                if (string.IsNullOrEmpty(lcdName))
                    return;

                string message = $"CAMCTRL|{cmd}|{lcdName}";

                if (MyAPIGateway.Session.IsServer)
                {
                    // Dedicated server: action fires server-side — use mod messaging (same-process)
                    MyAPIGateway.Utilities.SendModMessage(CTRL_MOD_CHANNEL, message);
                }
                else
                {
                    // Client (listen server / single-player): send over network
                    byte[] data = Encoding.UTF8.GetBytes(message);
                    MyAPIGateway.Multiplayer.SendMessageToServer(CTRL_MESSAGE_ID, data);
                }
            };
            action.Writer = (block, sb) => sb.Append(label);
            return action; // Caller registers via CustomActionGetter, not AddAction
        }

        private static void OnCustomActionGetter(IMyTerminalBlock block, List<IMyTerminalAction> actions)
        {
            if (!(block is IMyButtonPanel))
                return;

            if (_nextAction  != null) actions.Add(_nextAction);
            if (_prevAction  != null) actions.Add(_prevAction);
            if (_resetAction != null) actions.Add(_resetAction);
            if (_nextLoopAction != null) actions.Add(_nextLoopAction);
            if (_prevLoopAction != null) actions.Add(_prevLoopAction);
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
                            double x = double.Parse(parts[3], CultureInfo.InvariantCulture);
                            double y = double.Parse(parts[4], CultureInfo.InvariantCulture);
                            double z = double.Parse(parts[5], CultureInfo.InvariantCulture);

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
                                double.Parse(parts[4], CultureInfo.InvariantCulture),
                                double.Parse(parts[5], CultureInfo.InvariantCulture),
                                double.Parse(parts[6], CultureInfo.InvariantCulture)
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

            // Register terminal actions on the first tick after LoadData.
            // The terminal system is guaranteed to be ready by this point.
            if (_pendingActionRegistration)
            {
                _pendingActionRegistration = false;
                RegisterCameraControlActions();
            }

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
            bool isDedicatedServer = MyAPIGateway.Session.IsServer && MyAPIGateway.Utilities.IsDedicated;

            if (_isInitialized && !isDedicatedServer)
            {
                try
                {
                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(MESSAGE_ID, OnMessageReceived);
                }
                catch { }
            }

            // Do NOT reset _actionsRegistered here. SE's terminal action registry is a
            // global static that persists for the entire server process lifetime — it is
            // NOT cleared on world unload. Resetting the flag causes AddAction to be called
            // again on the next world load, appending duplicate action IDs to the same
            // list, which corrupts the button panel config UI.
        }
    }
}
