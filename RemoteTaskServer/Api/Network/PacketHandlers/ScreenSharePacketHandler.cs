﻿#region

using System;
using System.Collections;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WindowsInput;
using WindowsInput.Native;
using UlteriusServer.Api.Network.Messages;
using UlteriusServer.Api.Win32;
using UlteriusServer.Api.Win32.ScreenShare;
using UlteriusServer.Api.Win32.ScreenShare.DesktopDuplication;
using UlteriusServer.WebSocketAPI.Authentication;
using vtortola.WebSockets;
using static UlteriusServer.Api.UlteriusApiServer;
using ScreenShareService = UlteriusServer.Api.Services.LocalSystem.ScreenShareService;
using SystemInformation = UlteriusServer.Api.Network.Models.SystemInformation;

#endregion

namespace UlteriusServer.Api.Network.PacketHandlers
{
    internal class ScreenSharePacketHandler : PacketHandler
    {
        private AuthClient _authClient;
        private MessageBuilder _builder;
        private WebSocket _client;
        private long _lastLoopTime = Environment.TickCount;
        private Packet _packet;
        private int fps;
        private long lastFpsTime;
        private int lastLoopTime;

        [DllImport("Sas.dll", SetLastError = true)]
        public static extern void SendSAS(bool asUser);

        public void StopScreenShare()
        {
            try
            {
                _authClient.ShutDownScreenShare = true;
                Thread outtemp;
                if (!ScreenShareService.Streams.TryRemove(_authClient, out outtemp)) return;
                if (!RunningAsService)
                {
                    CleanUp();
                }
                if (!_client.IsConnected) return;
                var data = new
                {
                    streamStopped = true
                };
                _builder.WriteMessage(data);
            }
            catch (Exception e)
            {
                if (_client.IsConnected)
                {
                    var data = new
                    {
                        streamStopped = false,
                        message = e.Message
                    };
                    _builder.WriteMessage(data);
                }
            }
        }

        private void CleanUp()
        {
        }

        public void CheckServer()
        {
        }

        public void GetAvailableMonitors()
        {
            var activeDisplays = SystemInformation.Displays;
            var selectedDisplay = ScreenData.ActiveDisplay;
            var data = new
            {
                activeDisplays,
                selectedDisplay
            };
            _builder.WriteMessage(data);
        }

        public void SetActiveMonitor()
        {
            if (_packet.Args.ElementAt(0) == null)
            {
                ScreenData.ActiveDisplay = 0;
            }
            ScreenData.ActiveDisplay = Convert.ToInt32(_packet.Args[0]);
            var activeDisplays = SystemInformation.Displays;
            if (RunningAsService)
            {
              //  AgentClient.SetActiveMonitor(ScreenData.ActiveDisplay);
            }
            var data = new
            {
                selectedDisplay = ScreenData.ActiveDisplay,
                resolutionInformation = activeDisplays[ScreenData.ActiveDisplay].CurrentResolution
            };
            _builder.WriteMessage(data);
        }

        public void StartScreenShare()
        {
            try
            {
                if (ScreenShareService.Streams.ContainsKey(_authClient))
                {
                    var failData = new
                    {
                        cameraStreamStarted = false,
                        message = "Stream already created"
                    };
                    _builder.WriteMessage(failData);
                    return;
                }
                _authClient.ShutDownScreenShare = false;
                var stream = new Thread(GetScreenFrame) { IsBackground = true };
                ScreenShareService.Streams[_authClient] = stream;
                var data = new
                {
                    screenStreamStarted = true
                };
                _builder.WriteMessage(data);
                ScreenShareService.Streams[_authClient].Start();
            }
            catch (Exception exception)
            {
                var data = new
                {
                    cameraStreamStarted = false,
                    message = exception.Message
                };

                _builder.WriteMessage(data);
            }
        }

        private void SendGpuFrame(FinishedRegions[] gpuFrame)
        {
            if (gpuFrame == null)
            {
                return;
            }
            if (gpuFrame.Length == 0)
            {
                return;
            }
            foreach (var region in gpuFrame)
            {
                var data = ScreenData.PackScreenCaptureData(region.Frame, region.Destination);
                if (data == null || data.Length <= 0) continue;
                _builder.Endpoint = "screensharedata";
                _builder.WriteScreenFrame(data);
                region?.Dispose();
            }
        }

        private void GetScreenFrame()
        {
            while (_client != null && _client.IsConnected && _authClient != null &&
                   !_authClient.ShutDownScreenShare)
            {
                if (RunningAsService && DesktopWatcher.CurrentDesktop != null)
                {
                    Desktop.SetCurrent(DesktopWatcher.CurrentDesktop);
                }
                try
                {
                    var image = ScreenData.DesktopCapture();
                    if (image == null) continue;
                    if (image.UsingGpu)
                    {
                        SendGpuFrame(image.FinishedRegions);
                    }
                    else
                    {
                        SendPolledFrame(image.ScreenImage, image.Bounds);
                    }
                }
                catch (Exception e)
                {
                    // Console.WriteLine(e.Message + " " + e.StackTrace);
                }
            }
            Console.WriteLine("Screen Share Died");
        }

        private void SendPolledFrame(Bitmap screenImage, Rectangle bounds)
        {
            if (screenImage == null || bounds == Rectangle.Empty) return;
            var data = ScreenData.PackScreenCaptureData(screenImage, bounds);
            if (data == null || data.Length <= 0) return;
            _builder.Endpoint = "screensharedata";
            _builder.WriteScreenFrame(data);
            screenImage?.Dispose();
            data = null;
        }


        public override void HandlePacket(Packet packet)
        {
            if (RunningAsService && DesktopWatcher.CurrentDesktop != null)
            {
                Desktop.SetCurrent(DesktopWatcher.CurrentDesktop);
            }
            _client = packet.Client;
            _authClient = packet.AuthClient;
            _packet = packet;
            _builder = new MessageBuilder(_authClient, _client, _packet.EndPointName, _packet.SyncKey);
            switch (_packet.EndPoint)
            {
                case PacketManager.EndPoints.MouseDown:
                    HandleMouseDown();
                    break;
                case PacketManager.EndPoints.MouseUp:
                    HandleMouseUp();
                    break;
                case PacketManager.EndPoints.CtrlAltDel:
                    HandleCtrlAltDel();
                    break;
                case PacketManager.EndPoints.MouseScroll:
                    HandleScroll();
                    break;
                case PacketManager.EndPoints.LeftDblClick:
                    break;
                case PacketManager.EndPoints.KeyDown:
                    HandleKeyDown();
                    break;
                case PacketManager.EndPoints.RightDown:
                    RightDown();
                    break;
                case PacketManager.EndPoints.RightUp:
                    RightUp();
                    break;
                case PacketManager.EndPoints.KeyUp:
                    HandleKeyUp();
                    break;
                case PacketManager.EndPoints.FullFrame:
                    HandleFullFrame();
                    break;
                case PacketManager.EndPoints.RightClick:
                    HandleRightClick();
                    break;
                case PacketManager.EndPoints.SetActiveMonitor:
                    SetActiveMonitor();
                    break;
                case PacketManager.EndPoints.MouseMove:
                    HandleMoveMouse();
                    break;
                case PacketManager.EndPoints.CheckScreenShare:
                    CheckServer();
                    break;
                case PacketManager.EndPoints.StartScreenShare:
                    StartScreenShare();
                    break;
                case PacketManager.EndPoints.GetAvailableMonitors:
                    GetAvailableMonitors();
                    break;
                case PacketManager.EndPoints.StopScreenShare:
                    StopScreenShare();
                    break;
            }
        }

        private void HandleCtrlAltDel()
        {
            SendSAS(false);
        }

        private void RightUp()
        {
            new InputSimulator().Mouse.RightButtonUp();
        }

        private void RightDown()
        {
            new InputSimulator().Mouse.RightButtonDown();
        }

        private void HandleFullFrame()
        {
            using (var grab = ScreenData.CaptureDesktop())
            {
                var imgData = ScreenData.ImageToByteArray(grab);
                var monitors = SystemInformation.Displays;
                Rectangle bounds;
                if (monitors.Count > 0 && monitors.ElementAt(ScreenData.ActiveDisplay) != null)
                {
                    var activeDisplay = monitors[ScreenData.ActiveDisplay];
                    bounds = new Rectangle
                    {
                        X = activeDisplay.CurrentResolution.X,
                        Y = activeDisplay.CurrentResolution.Y,
                        Width = activeDisplay.CurrentResolution.Width,
                        Height = activeDisplay.CurrentResolution.Height
                    };
                }
                else
                {
                    bounds = Display.GetWindowRectangle();
                }
                var screenBounds = new
                {
                    top = bounds.Top,
                    bottom = bounds.Bottom,
                    left = bounds.Left,
                    right = bounds.Right,
                    height = bounds.Height,
                    width = bounds.Width,
                    x = bounds.X,
                    y = bounds.Y,
                    empty = bounds.IsEmpty,
                    location = bounds.Location,
                    size = bounds.Size
                };
                var frameData = new
                {
                    screenBounds,
                    frameData = imgData.Select(b => (int)b).ToArray()
                };
                _builder.WriteMessage(frameData);
            }
        }

        private void HandleKeyUp()
        {
            var keyCodes = ((IEnumerable)_packet.Args[0]).Cast<object>()
                .Select(x => x.ToString())
                .ToList();
            var codes =
                keyCodes.Select(code => ToHex(int.Parse(code.ToString())))
                    .Select(hexString => Convert.ToInt32(hexString, 16))
                    .ToList();

            foreach (var code in codes)
            {
                var virtualKey = (VirtualKeyCode)code;
                new InputSimulator().Keyboard.KeyUp(virtualKey);
            }
        }


        private string ToHex(int value)
        {
            return $"0x{value:X}";
        }

        private void HandleKeyDown()
        {
            var keyCodes = ((IEnumerable)_packet.Args[0]).Cast<object>()
                .Select(x => x.ToString())
                .ToList();
            var codes =
                keyCodes.Select(code => ToHex(int.Parse(code.ToString())))
                    .Select(hexString => Convert.ToInt32(hexString, 16))
                    .ToList();
            foreach (var code in codes)
            {
                var virtualKey = (VirtualKeyCode)code;
                new InputSimulator().Keyboard.KeyDown(virtualKey);
            }
        }

        private void HandleScroll()
        {
            var delta = Convert.ToInt32(_packet.Args[0], CultureInfo.InvariantCulture);
            delta = ~delta;
            var positive = delta > 0;
            var direction = positive ? 10 : -10;
            new InputSimulator().Mouse.VerticalScroll(direction);
        }

        private static Point Translate(Point point, Size from, Size to)
        {
            return new Point(point.X * to.Width / from.Width, point.Y * to.Height / from.Height);
        }

        private static Point GetRelativeCoordinates(Point absoluteCoordinates)
        {
            var screen = Screen.FromPoint(absoluteCoordinates);
            return new Point(absoluteCoordinates.X - screen.Bounds.Left, absoluteCoordinates.Y - screen.Bounds.Top);
        }

        private void HandleMoveMouse()
        {
            try
            {

                int y = Convert.ToInt16(_packet.Args[0], CultureInfo.InvariantCulture);
                int x = Convert.ToInt16(_packet.Args[1], CultureInfo.InvariantCulture);
                var bounds = Display.GetWindowRectangle();
                x = checked((int)Math.Round(x * (65535 / (double)bounds.Width)));
                y = checked((int)Math.Round(y * (65535 / (double)bounds.Height)));
                new InputSimulator().Mouse.MoveMouseTo(x, y);
            }

            catch
            {
                //Console.WriteLine("Error moving mouse");
            }
        }

        private void HandleRightClick()
        {
            new InputSimulator().Mouse.RightButtonClick();
        }

        private void HandleMouseUp()
        {
            new InputSimulator().Mouse.LeftButtonUp();
        }

        private void HandleMouseDown()
        {
            new InputSimulator().Mouse.LeftButtonDown();
        }
    }
}