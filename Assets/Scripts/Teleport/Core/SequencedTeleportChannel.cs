﻿using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeBox.Teleport.Core
{
    public class SequencedTeleportChannel : BaseTeleportProxyChannel
    {
        private const int ACK_TIMEOUT_DURATION_IN_TICKS = 5000000;
        private const int ACK_TIMEOUT_INCREMENT_PER_MESSAGE_COUNT = 50;

        private class OutboxItem
        {
            public byte[] data;
            public long nextSendTime;
        }

        private class InboxItem
        {
            public byte[] data;
            public int startIndex;
            public int length;
        }


        private ushort _outgoingSequence;
        private int _lastReceiveIndex;
        private int _lastProcessedReceiveIndex;
        private Dictionary<ushort, InboxItem> _inbox;
        private Dictionary<ushort, OutboxItem> _outbox;
        private bool _sendAcks;
        private object _outboxLock;
        private ArrayQueue<ushort> _pendingAcksQueue;

        public SequencedTeleportChannel() : this(new SimpleTeleportChannel()) { }

        public SequencedTeleportChannel(BaseTeleportChannel internalChannel) : this(internalChannel, true)
        {            
        }

        public SequencedTeleportChannel(BaseTeleportChannel internalChannel, bool sendAcks) : base(internalChannel)
        {
            _sendAcks = sendAcks;
            _outgoingSequence = 0;
            _lastReceiveIndex = -1;
            _lastProcessedReceiveIndex = -1;
            _inbox = new Dictionary<ushort, InboxItem>();
            _outbox = new Dictionary<ushort, OutboxItem>();
            _outboxLock = new object();
            _pendingAcksQueue = new ArrayQueue<ushort>(80960);
        }

        public override void Receive(byte[] data, int startIndex, int length)
        {            
            var processedLength = 0;
            ushort sequenceNumber;
            var copiedData = new byte[length];
            Array.Copy(data, startIndex, copiedData, 0, length);
            
            if (_sendAcks && copiedData[0] == 0xff && copiedData[1] == 0xff)
            {
                
                lock (_outboxLock)
                {
                    sequenceNumber = BitConverter.ToUInt16(copiedData, 2);
                    _outbox.Remove(sequenceNumber);
                }
                return;
            }
            sequenceNumber = BitConverter.ToUInt16(copiedData, 0);
            processedLength += sizeof(ushort);
            if (_inbox.ContainsKey(sequenceNumber) || _lastProcessedReceiveIndex >= sequenceNumber)
            {
                Debug.LogWarning("Got same sequence twice: " + sequenceNumber);
                return;
            }
            _inbox[sequenceNumber] = new InboxItem() { data = copiedData, startIndex =  processedLength, length = length - processedLength };
            if (sequenceNumber > _lastReceiveIndex)
            {
                _lastReceiveIndex = sequenceNumber;
            }
            if (_sendAcks)
            {
                byte[] ackData = new byte[] { 0xff, 0xff, 0, 0 };
                Array.Copy(BitConverter.GetBytes(sequenceNumber), 0, ackData, 2, 2);
                Send(ackData);
                _pendingAcksQueue.Enqueue(sequenceNumber);           
                ProcessOutbox();
            }            
            ProcessInbox();
        }

        public override void Upkeep()
        {
            InternalChannel.Upkeep();
            int maxAcks = 1;
            ushort sequenceNumber;
            while (_pendingAcksQueue.Count > 0 && maxAcks > 0)
            {
                
                maxAcks--;
                sequenceNumber = _pendingAcksQueue.Dequeue();
                byte[] ackData = new byte[] { 0xff, 0xff, 0, 0 };
                Array.Copy(BitConverter.GetBytes(sequenceNumber), 0, ackData, 2, 2);
                Send(ackData);
            }
        }

        public override void Send(byte[] data)
        {
            InternalChannel.Send(data);
            ProcessOutbox();
        }

        public override byte[] PrepareToSend(byte[] data)
        {
            data = InternalChannel.PrepareToSend(data);
            byte[] sequenceBytes = BitConverter.GetBytes(_outgoingSequence);
            var newData = new byte[data.Length + sequenceBytes.Length];
            Array.Copy(sequenceBytes, 0, newData, 0, sequenceBytes.Length);
            Array.Copy(data, 0, newData, sequenceBytes.Length, data.Length);
            if (_sendAcks)
            {
                lock (_outboxLock)
                {
                    _outbox[_outgoingSequence] = new OutboxItem() { data = newData, nextSendTime = DateTime.UtcNow.Ticks + ACK_TIMEOUT_DURATION_IN_TICKS };
                }
            }
            _outgoingSequence++;
            return newData;
        }

        private void ProcessOutbox()
        {
            if (_outbox.Count == 0 || !_sendAcks)
            {
                return;
            }
            ushort seqId;
            OutboxItem outboxItem;
            lock (_outboxLock)
            {
                foreach (var p in _outbox)
                {
                    seqId = p.Key;
                    outboxItem = p.Value;
                    if (outboxItem.nextSendTime < DateTime.UtcNow.Ticks)                    
                    {
                        outboxItem.nextSendTime = DateTime.UtcNow.Ticks + ACK_TIMEOUT_DURATION_IN_TICKS * (1 + (_outbox.Count / ACK_TIMEOUT_INCREMENT_PER_MESSAGE_COUNT));
                        InternalChannel.Send(outboxItem.data);
                    }
                }
            }
        }

        private void ProcessInbox()
        {
            if (_inbox.Count == 0)
            {
                return;
            }
            InboxItem inboxItem;
            ushort nextIndex;
            while (_lastReceiveIndex > _lastProcessedReceiveIndex)
            {
                nextIndex = (ushort)(_lastProcessedReceiveIndex + 1);
                if (!_inbox.TryGetValue(nextIndex, out inboxItem))
                {
                    break;
                }
                _inbox.Remove(nextIndex);
                InternalChannel.Receive(inboxItem.data, inboxItem.startIndex, inboxItem.length);
                _lastProcessedReceiveIndex = nextIndex;
            }
        }


    }

}
