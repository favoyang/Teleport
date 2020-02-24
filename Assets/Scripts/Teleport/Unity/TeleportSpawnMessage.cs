﻿using System;
using System.IO;
using DeBox.Teleport.Core;
using UnityEngine;

namespace DeBox.Teleport.Unity
{
    public class TeleportSpawnMessage : BaseTeleportMessage, ITeleportTimedMessage
    {
        public float Timestamp { get; private set; }
        public ushort SpawnId { get; private set; }

        public override byte MsgTypeId => TeleportMsgTypeIds.Spawn;

        public GameObject SpawnedObject { get; private set; }
        public Vector3 Position { get; private set; }

        private ITeleportObjectSpawner _spawner;
        private TeleportReader _reader;
        private object _objectConfig;

        public TeleportSpawnMessage() {}

        public TeleportSpawnMessage(ITeleportObjectSpawner spawner, Vector3 position, object objectConfig)
        {
            _objectConfig = objectConfig;
            _spawner = spawner;
            Position = position;
        }

        public void OnTimedPlayback()
        {
            var instance = _spawner.CreateInstance();
            _spawner.OnClientSpawn(_reader, instance);
            _reader.Close();
            SpawnedObject = instance;
            SpawnedObject.transform.position = Position;
        }

        public override void Deserialize(TeleportReader reader)
        {
            base.Deserialize(reader);
            SpawnId = reader.ReadUInt16();
            Position = reader.ReadVector3();
            _spawner = TeleportManager.Main.GetClientSpawner(SpawnId);
            // The reader will be closed by the time we use it, so we create a new reader
            var rawData = ((MemoryStream)reader.BaseStream).ToArray();
            var data = new byte[rawData.Length - reader.BaseStream.Position];
            Array.Copy(rawData, reader.BaseStream.Position, data, 0, data.Length);
            _reader = new TeleportReader(data);
        }

        public override void Serialize(TeleportWriter writer)
        {
            var instance = _spawner.CreateInstance();
            instance.transform.position = Position;
            base.Serialize(writer);
            writer.Write(_spawner.SpawnId);
            writer.Write(Position);
            _spawner.OnServerSpawn(writer, instance, _objectConfig);
            SpawnedObject = instance;
        }

        public void SetTimestamp(float timestamp)
        {
            Timestamp = timestamp;
        }
    }
}

