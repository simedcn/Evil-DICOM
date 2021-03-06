﻿#region

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using EvilDICOM.Core.Logging;
using EvilDICOM.Network.DIMSE;
using EvilDICOM.Network.Enums;
using EvilDICOM.Network.Interfaces;
using EvilDICOM.Network.Messaging;
using EvilDICOM.Network.PDUs.Items;
using EvilDICOM.Network.Processors;
using EvilDICOM.Network.Readers;

#endregion

namespace EvilDICOM.Network
{
    public class Association
    {
        private bool _abortRequested;
        private bool _cancelRequested;

        public Association(DICOMServiceClass serviceClass, TcpClient client)
        {
            ServiceClass = serviceClass;
            Stream = new BufferedStream(client.GetStream());
            Reader = new NetworkBinaryReader(Stream);
            PresentationContexts = new List<PresentationContext>();
            IpAddress = ((IPEndPoint) client.Client.RemoteEndPoint).Address.ToString();
            Port = ((IPEndPoint) client.Client.RemoteEndPoint).Port;
            PDUProcessor = new PDUProcessor();
            PDataProcessor = new PDataProcessor();
            State = NetworkState.IDLE;
            OutboundMessages = new ConcurrentQueue<AbstractDIMSEBase>();
        }

        public DICOMServiceClass ServiceClass { get; private set; }

        /// <summary>
        ///     The agreed upon presentation context of the association
        /// </summary>
        public List<PresentationContext> PresentationContexts { get; set; }

        /// <summary>
        ///     The last time of communication of this association
        /// </summary>
        public DateTime LastActive { get; set; }

        /// <summary>
        ///     The user info containing maximum PDataTF packet size
        /// </summary>
        public UserInfo UserInfo { get; set; }


        public PDUProcessor PDUProcessor { get; set; }
        public PDataProcessor PDataProcessor { get; set; }

        public EventLogger Logger
        {
            get { return ServiceClass.Logger; }
        }

        public ConcurrentQueue<AbstractDIMSEBase> OutboundMessages { get; set; }
        public BufferedStream Stream { get; private set; }
        public NetworkBinaryReader Reader { get; private set; }
        public NetworkState State { get; set; }

        public void Listen(TimeSpan? maxWaitTime = null)
        {
            maxWaitTime = maxWaitTime ?? TimeSpan.FromSeconds(25);
            var clock = new Stopwatch();
            clock.Start();
            while (State != NetworkState.CLOSING_ASSOCIATION && clock.Elapsed < maxWaitTime)
            {
                if (_abortRequested)
                {
                    HandleAbort();
                    break;
                }
                if (_cancelRequested) HandleCancel();

                var message = Read();
                if (message != null)
                {
                    clock.Restart();
                    Process(message);
                    Stream.Flush();
                    clock.Restart();
                }
            }
        }

        private void HandleCancel()
        {
            AbstractDIMSEBase cancel;
            OutboundMessages.TryPeek(out cancel);
            if (cancel is CCancel)
            {
                OutboundMessages.TryDequeue(out cancel);
                Stream.Flush();
                PDataMessenger.Send(cancel, this);
            }
        }

        private void HandleAbort()
        {
            AssociationMessenger.SendAbort(this);
            State = NetworkState.CLOSING_ASSOCIATION;
        }

        public IMessage Read()
        {
            try
            {
                var message = PDUReader.Read(Reader);
                return message;
            }
            catch (Exception e)
            {
                Logger.Log(e.Message);
                return null;
            }
        }

        public void Process(IMessage message)
        {
            if (message != null)
                switch (message.Type)
                {
                    case MessageType.PDU:
                        PDUProcessor.Process(message, this);
                        break;
                    case MessageType.PDATA_TF:
                        PDataProcessor.Process(message, this);
                        break;
                    case MessageType.ERROR:
                        ErrorProcessor.Process(message);
                        break;
                }
        }

        public void Release()
        {
            State = NetworkState.CLOSING_ASSOCIATION;
            try
            {
                Stream.Flush();
            }
            catch(Exception e)
            {
                Debug.WriteLine(e);
            }

        }

        public void RequestAbort()
        {
            _abortRequested = true;
        }

        public override string ToString()
        {
            return string.Format("ASSOCIATION\nIP Address : {0}\nPort :{1}\n", IpAddress, Port);
        }

        public void Cancel(CCancel cancel)
        {
            _cancelRequested = true;
            OutboundMessages.Enqueue(cancel);
        }

        #region ASSOCIATION IDENTITY

        /// <summary>
        ///     The foreign AeTitle
        /// </summary>
        public string AeTitle { get; set; }

        /// <summary>
        ///     The ip address of the foreign service class
        /// </summary>
        public string IpAddress { get; set; }

        /// <summary>
        ///     The port of the foreign service class
        /// </summary>
        public int Port { get; set; }

        #endregion

        #region MESSAGING

        public void SendMessage(byte[] message)
        {
            if (message != null && Stream.CanWrite)
                Stream.Write(message, 0, message.Length);
        }

        public void SendMessage(IPDU message)
        {
            SendMessage(message.Write());
        }

        #endregion
    }
}