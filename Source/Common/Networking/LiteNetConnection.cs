using LiteNetLib;
using Multiplayer.Common.Networking.Packet;

namespace Multiplayer.Common
{
    public class LiteNetConnection(NetPeer peer) : ConnectionBase
    {
        public readonly NetPeer peer = peer;

        // Only a hint at who the remote is: players behind one NAT share an address, so a housemate
        // can match; and a player whose address changed (mobile, VPN, or v4 vs v6 across the two
        // NetManagers) won't match their own earlier connection.
        public override object? RemoteIdentity => peer.Address;

        /// <summary>
        /// Sends without first checking the peer's transport state. That state changes on LiteNetLib's
        /// own thread, so it can go stale between <see cref="ConnectionBase.Send"/> approving the send
        /// against the protocol state and the packet arriving here; a check on this side can never be
        /// race-free. LiteNetLib discards sends to a peer that has gone away, silently and without
        /// throwing, so there is nothing left for one to do.
        /// </summary>
        protected override void SendRaw(byte[] raw, bool reliable)
        {
            peer.Send(raw, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable);
        }

        protected override void OnClose(ServerDisconnectPacket? goodbye)
        {
            if (goodbye.HasValue)
                peer.Disconnect(goodbye.Value.Serialize().data);
            else
                peer.Disconnect();
        }

        public override void OnKeepAliveArrived(bool idMatched)
        {
            // Latency already handled by LiteNetLib. This can be as low as 0ms because LNL spawns its own thread for
            // receiving packets and immediately processes its own internal keep alive packet (called Ping-Pong).
            // This is handled only on the server-side in MpServerNetListener
        }

        public override string ToString()
        {
            return $"NetConnection ({peer}) ({username})";
        }
    }
}
