using PKHeX.Core;
using RaidCrawler.Core.Interfaces;
using RaidCrawler.Core.Structures;
using SysBot.Base;
using System.Net.Sockets;
using System.Text;
using static SysBot.Base.SwitchButton;
using static SysBot.Base.SwitchStick;

namespace RaidCrawler.Core.Connection
{
    public class ConnectionWrapperAsync : Offsets
    {
        public readonly ISwitchConnectionAsync Connection;
        public bool Connected { get => Connection is not null && IsConnected; }
        private bool IsConnected { get; set; }
        private readonly bool CRLF;
        private readonly Action<string> _statusUpdate;
        private static ulong BaseBlockKeyPointer = 0;

        public ConnectionWrapperAsync(SwitchConnectionConfig config, Action<string> statusUpdate)
        {
            Connection = config.Protocol switch
            {
                SwitchProtocol.USB => new SwitchUSBAsync(config.Port),
                _ => new SwitchSocketAsync(config),
            };

            CRLF = config.Protocol is SwitchProtocol.WiFi;
            _statusUpdate = statusUpdate;
        }

        public async Task<(bool, string)> Connect(CancellationToken token)
        {
            if (Connected)
                return (true, "");

            try
            {
                _statusUpdate("Connecting...");
                Connection.Connect();
                BaseBlockKeyPointer = await Connection.PointerAll(BlockKeyPointer, token).ConfigureAwait(false);
                IsConnected = true;
                _statusUpdate("Connected!");
                return (true, "");
            }
            catch (SocketException e)
            {
                IsConnected = false;
                return (false, e.Message);
            }
        }

        public async Task<(bool, string)> DisconnectAsync(CancellationToken token)
        {
            if (!Connected)
                return (true, "");

            try
            {
                _statusUpdate("Disconnecting controller...");
                await Connection.SendAsync(SwitchCommand.DetachController(CRLF), token).ConfigureAwait(false);

                _statusUpdate("Disconnecting...");
                Connection.Disconnect();
                IsConnected = false;
                _statusUpdate("Disconnected!");
                await HardStop(token).ConfigureAwait(false);
                return (true, "");
            }
            catch (SocketException e)
            {
                IsConnected = false;
                await HardStop(token).ConfigureAwait(false);
                return (false, e.Message);
            }
        }

        public async Task<int> GetStoryProgress(CancellationToken token)
        {
            for (int i = DifficultyFlags.Count - 1; i >= 0; i--)
            {
                // See https://github.com/Lincoln-LM/sv-live-map/pull/43
                var block = await ReadSaveBlock(DifficultyFlags[i], 1, token).ConfigureAwait(false);
                if (block[0] == 2)
                    return i + 1;
            }
            return 0;
        }

        private async Task<byte[]> ReadSaveBlock(uint key, int size, CancellationToken token)
        {
            var block_ofs = await SearchSaveKey(key, token).ConfigureAwait(false);
            var data = await Connection.ReadBytesAbsoluteAsync(block_ofs + 8, 0x8, token).ConfigureAwait(false);
            block_ofs = BitConverter.ToUInt64(data, 0);

            var block = await Connection.ReadBytesAbsoluteAsync(block_ofs, size, token).ConfigureAwait(false);
            return DecryptBlock(key, block);
        }

        private async Task<byte[]> ReadSaveBlockObject(uint key, CancellationToken token)
        {
            var header_ofs = await SearchSaveKey(key, token).ConfigureAwait(false);
            var data = await Connection.ReadBytesAbsoluteAsync(header_ofs + 8, 8, token).ConfigureAwait(false);
            header_ofs = BitConverter.ToUInt64(data);

            var header = await Connection.ReadBytesAbsoluteAsync(header_ofs, 5, token).ConfigureAwait(false);
            header = DecryptBlock(key, header);

            var size = BitConverter.ToUInt32(header.AsSpan()[1..]);
            var obj = await Connection.ReadBytesAbsoluteAsync(header_ofs, (int)size + 5, token).ConfigureAwait(false);
            return DecryptBlock(key, obj)[5..];
        }

        public async Task<byte[]> ReadBlockDefault(uint key, string? cache, bool force, CancellationToken token)
        {
            var folder = Path.Combine(Directory.GetCurrentDirectory(), "cache");
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, cache ?? "");
            if (force is false && cache is not null && File.Exists(path))
                return File.ReadAllBytes(path);

            var bin = await ReadSaveBlockObject(key, token).ConfigureAwait(false);
            File.WriteAllBytes(path, bin);
            return bin;
        }

        private async Task<ulong> SearchSaveKey(uint key, CancellationToken token)
        {
            var data = await Connection.ReadBytesAbsoluteAsync(BaseBlockKeyPointer + 8, 16, token).ConfigureAwait(false);
            var start = BitConverter.ToUInt64(data.AsSpan()[..8]);
            var end = BitConverter.ToUInt64(data.AsSpan()[8..]);

            while (start < end)
            {
                var block_ct = (end - start) / 48;
                var mid = start + (block_ct >> 1) * 48;

                data = await Connection.ReadBytesAbsoluteAsync(mid, 4, token).ConfigureAwait(false);
                var found = BitConverter.ToUInt32(data);
                if (found == key)
                    return mid;

                if (found >= key)
                    end = mid;
                else start = mid + 48;
            }
            return start;
        }

        private static byte[] DecryptBlock(uint key, byte[] block)
        {
            var rng = new SCXorShift32(key);
            for (int i = 0; i < block.Length; i++)
                block[i] = (byte)(block[i] ^ rng.Next());
            return block;
        }

        private async Task Click(SwitchButton button, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.Click(button, CRLF), token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        private async Task Touch(int x, int y, int hold, int delay, CancellationToken token)
        {
            var command = Encoding.ASCII.GetBytes($"touchHold {x} {y} {hold}{(CRLF ? "\r\n" : "")}");
            await Connection.SendAsync(command, token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        private async Task SetStick(SwitchStick stick, short x, short y, int hold, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.SetStick(stick, x, y, CRLF), token).ConfigureAwait(false);
            await Task.Delay(hold, token).ConfigureAwait(false);
            await Connection.SendAsync(SwitchCommand.SetStick(stick, 0, 0, CRLF), token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        private async Task PressAndHold(SwitchButton b, int hold, int delay, CancellationToken token)
        {
            await Connection.SendAsync(SwitchCommand.Hold(b, CRLF), token).ConfigureAwait(false);
            await Task.Delay(hold, token).ConfigureAwait(false);
            await Connection.SendAsync(SwitchCommand.Release(b, CRLF), token).ConfigureAwait(false);
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        // Thank you to Anubis for sharing a more optimized routine, as well as CloseGame(), StartGame(), and SaveGame()!
        public async Task AdvanceDate(CancellationToken token)
        {
            // Not great, but when adding/removing clicks, make sure to account for command count for an accurate StreamerView progress bar.
            await Click(B, 0_500, token).ConfigureAwait(false); // Sometimes it seems like the first command doesn't go through so send this just in case

            // HOME Menu
            await Click(HOME, 0_800, token).ConfigureAwait(false);

            // Navigate to Settings
            await Touch(840, 540, 0_050, 0, token).ConfigureAwait(false);
            await Click(A, 1_250, token).ConfigureAwait(false);

            // Scroll to bottom
            await PressAndHold(DDOWN, 2_000, 0_100, token).ConfigureAwait(false);

            // Navigate to "Date and Time"
            await Click(A, 0_300, token).ConfigureAwait(false);
            // Hold down to overshoot Date/Time by one. DUP to recover.
            await SetStick(LEFT, 0, -30000, 0_830, 0, token).ConfigureAwait(false);
            await SetStick(LEFT, 0, 0, 0_500, 0, token).ConfigureAwait(false);

            await Click(A, 0_100, token).ConfigureAwait(false);
            await Touch(950, 400, 0_100, 0_300, token).ConfigureAwait(false);
            for (int i = 0; i < 6; i++) await Click(DRIGHT, 0_050, token).ConfigureAwait(false);
            await Touch(950, 400, 0_100, 0_300, token).ConfigureAwait(false);
            await Click(A, 0_100, token).ConfigureAwait(false);

            await Click(A, 0_200, token).ConfigureAwait(false);

            await Click(DUP, 0_105, token).ConfigureAwait(false);
            for (int i = 0; i < 6; i++) await Click(DRIGHT, 0_105, token).ConfigureAwait(false);
            await Click(A, 0_500, token).ConfigureAwait(false);

            // Return to game
            await Click(HOME, 0_800, token).ConfigureAwait(false);
            await Click(HOME, 2_200, token).ConfigureAwait(false);
        }

        public async Task CloseGame(CancellationToken token)
        {
            // Close out of the game
            _statusUpdate("Closing the game!");
            await Click(B, 0_500, token).ConfigureAwait(false);
            await Click(HOME, 2_000, token).ConfigureAwait(false);
            await Click(X, 1_000, token).ConfigureAwait(false);
            await Click(A, 5_500, token).ConfigureAwait(false);
            _statusUpdate("Closed out of the game!");
        }

        public async Task StartGame(CancellationToken token)
        {
            // Open game.
            _statusUpdate("Starting the game!");
            await Click(A, 1_000, token).ConfigureAwait(false);

            // Attempt to dodge an update prompt;
            await Click(DUP, 0_600, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);

            // If they have DLC on the system and can't use it, requires an UP + A to start the game.
            // Should be harmless otherwise since they'll be in loading screen.
            await Click(DUP, 0_600, token).ConfigureAwait(false);
            await Click(A, 0_600, token).ConfigureAwait(false);

            // Switch Logo and game load screen
            await Task.Delay(17_000, token).ConfigureAwait(false);

            for (int i = 0; i < 20; i++)
                await Click(A, 1_000, token).ConfigureAwait(false);

            _statusUpdate("Back in the overworld! Refreshing the base block key pointer...");
            BaseBlockKeyPointer = await Connection.PointerAll(BlockKeyPointer, token).ConfigureAwait(false);
        }

        public async Task SaveGame(IDateAdvanceConfig config, CancellationToken token)
        {
            _statusUpdate("Saving the game...");
            // B out in case we're in some menu.
            for (int i = 0; i < 4; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);

            // Open the menu and save.
            await Click(X, 1_000, token).ConfigureAwait(false);
            await Click(R, 1_000, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);
            await Click(A, 3_000 + config.SaveGameDelay, token).ConfigureAwait(false);

            // Return to overworld.
            for (int i = 0; i < 4; i++)
                await Click(B, 0_500, token).ConfigureAwait(false);
            _statusUpdate("Game saved!");
        }

        private static void UpdateProgressBar(Action<int>? action, int steps)
        {
            if (action is null)
                return;

            action.Invoke(steps);
        }

        public async Task HardStop(CancellationToken token)
        {
            await SetStick(LEFT, 0, 0, 0_500, 0_500, token).ConfigureAwait(false);
        }
    }
}
