using System.Collections.Generic;
using UnityEngine;

namespace SubLevelPhysics
{
	/// <summary>Multiplayer (spike). The server owns every sublevel; clients get a copy.
	/// 1. Chunks: vanilla ChunkManager.SendChunksToClients only sends for observers that name a client entity id, and
	///    the plot's own observers use -1. So the server adds one observer per plot per connected player, naming that
	///    player: vanilla then streams the plot's chunks (and later block changes) to that client.
	/// 2. Pose: NetPackageSubLevelPose, 10 times a second, lists every sublevel (id, chunk area, pose, motion). A client
	///    builds a local copy (root + kinematic body + block colliders + its own view observer so its mesher draws the
	///    chunks) the first time it sees an id, follows the pose, and drops copies the server no longer lists.</summary>
	internal static class MpSync
	{
		private static float nextPose, nextObs;
		public static int PacketsReceived;
		public static float LastPacketAt;

		private static ConnectionManager CM => SingletonMonoBehaviour<ConnectionManager>.Instance;

		private static bool Ready => CM != null && NetPackageManager.PackageMappings != null && GameManager.Instance != null
			&& GameManager.Instance.gameStateManager != null && GameManager.Instance.gameStateManager.IsGameStarted();

		/// <summary>Server, every frame (from PlotSpace.OnGameUpdate once the save's sublevels are restored).</summary>
		internal static void Tick(World world)
		{
			if (!Ready || !CM.IsServer) return;
			float now = Time.time;
			if (now >= nextObs) { nextObs = now + 1f; SyncObservers(world); }
			if (now >= nextPose && CM.Clients != null && CM.Clients.Count > 0)
			{
				nextPose = now + 0.1f;
				CM.SendPackage(NetPackageManager.GetPackage<NetPackageSubLevelPose>().Setup(PlotSpace.All));
			}
		}

		private static readonly HashSet<int> present = new HashSet<int>();
		private static readonly List<int> gone = new List<int>();

		private static void SyncObservers(World world)
		{
			present.Clear();
			if (CM.Clients != null)
				foreach (ClientInfo ci in CM.Clients.List)
					if (ci != null && ci.entityId != -1 && world.GetEntity(ci.entityId) is EntityPlayer) present.Add(ci.entityId);
			foreach (Plot p in PlotSpace.All)
			{
				if (p.Remote) continue;
				gone.Clear();
				foreach (int id in p.ClientObservers.Keys) if (!present.Contains(id)) gone.Add(id);
				foreach (int id in gone)
				{
					world.m_ChunkManager.RemoveChunkObserver(p.ClientObservers[id]);
					p.ClientObservers.Remove(id);
					Log.Out("[SubLevel] MP: stopped streaming sublevel " + p.Id + " to entity " + id);
				}
				foreach (int id in present)
				{
					if (p.ClientObservers.ContainsKey(id)) continue;
					Vector3 obsPos = new Vector3(p.Cx * 16 + 8, 0f, p.Cz * 16 + 8);
					p.ClientObservers[id] = world.m_ChunkManager.AddChunkObserver(obsPos, false, p.StageRadius - 1, id);
					Log.Out("[SubLevel] MP: streaming sublevel " + p.Id + " (" + (p.Keys.Count + p.SupportKeys.Count) + " chunks) to entity " + id);
				}
			}
		}

		/// <summary>Client: the server's list of sublevels arrived.</summary>
		internal static void ClientApply(World world, List<NetPackageSubLevelPose.Entry> list)
		{
			if (CM == null || CM.IsServer || !PlotSpace.ClientReady(world)) return;
			PacketsReceived++;
			LastPacketAt = Time.realtimeSinceStartup;
			var seen = new HashSet<string>();
			foreach (NetPackageSubLevelPose.Entry e in list)
			{
				seen.Add(e.Id);
				Plot p = PlotSpace.FindById(e.Id);
				bool fresh = false;
				if (p == null) { p = PlotSpace.CreateRemote(e.Id, e.Cx, e.Cz, e.Radius); fresh = true; }
				if (p == null || !p.Remote || p.Driver == null) continue;
				p.Driver.ApplyRemote(e.Pos, e.Rot, e.Vel, e.YawRate, fresh);
			}
			var drop = new List<Plot>();
			foreach (Plot p in PlotSpace.All) if (p.Remote && !seen.Contains(p.Id)) drop.Add(p);
			foreach (Plot p in drop) PlotSpace.DestroyRemote(p);
		}
	}

	public class NetPackageSubLevelPose : NetPackage
	{
		public struct Entry
		{
			public string Id;
			public int Cx, Cz, Radius;
			public Vector3 Pos, Vel;
			public Quaternion Rot;
			public float YawRate;
		}

		private List<Entry> list = new List<Entry>();

		public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

		public NetPackageSubLevelPose Setup(IList<Plot> plots)
		{
			list = new List<Entry>();
			foreach (Plot p in plots)
			{
				if (p.Remote || p.Driver == null || p.Rb == null) continue;
				list.Add(new Entry { Id = p.Id, Cx = p.Cx, Cz = p.Cz, Radius = p.PlotRadius, Pos = p.Driver.Pose, Rot = p.Rb.rotation,
					Vel = p.Driver.Velocity, YawRate = p.Driver.YawRate });
			}
			return this;
		}

		public override void read(PooledBinaryReader _reader)
		{
			int n = _reader.ReadUInt16();
			list = new List<Entry>(n);
			for (int i = 0; i < n; i++)
			{
				var e = new Entry();
				e.Id = _reader.ReadString();
				e.Cx = _reader.ReadInt32(); e.Cz = _reader.ReadInt32(); e.Radius = _reader.ReadInt32();
				e.Pos = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
				e.Rot = new Quaternion(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
				e.Vel = new Vector3(_reader.ReadSingle(), _reader.ReadSingle(), _reader.ReadSingle());
				e.YawRate = _reader.ReadSingle();
				list.Add(e);
			}
		}

		public override void write(PooledBinaryWriter _writer)
		{
			base.write(_writer);
			System.IO.BinaryWriter w = _writer;   // base type: the game's span overloads are not visible to this net48 build
			w.Write((ushort)list.Count);
			foreach (Entry e in list)
			{
				w.Write(e.Id ?? "");
				w.Write(e.Cx); w.Write(e.Cz); w.Write(e.Radius);
				w.Write(e.Pos.x); w.Write(e.Pos.y); w.Write(e.Pos.z);
				w.Write(e.Rot.x); w.Write(e.Rot.y); w.Write(e.Rot.z); w.Write(e.Rot.w);
				w.Write(e.Vel.x); w.Write(e.Vel.y); w.Write(e.Vel.z);
				w.Write(e.YawRate);
			}
		}

		public override void ProcessPackage(World _world, GameManager _callbacks)
		{
			if (_world == null) return;
			MpSync.ClientApply(_world, list);
		}
	}
}
