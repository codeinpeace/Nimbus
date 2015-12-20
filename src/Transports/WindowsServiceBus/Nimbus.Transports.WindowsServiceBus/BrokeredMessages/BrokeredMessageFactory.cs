﻿using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ServiceBus.Messaging;
using Nimbus.Configuration.LargeMessages.Settings;
using Nimbus.Extensions;
using Nimbus.Infrastructure;
using Nimbus.Infrastructure.Dispatching;
using Nimbus.MessageContracts.Exceptions;

namespace Nimbus.Transports.WindowsServiceBus.BrokeredMessages
{
    internal class BrokeredMessageFactory : IBrokeredMessageFactory
    {
        private readonly MaxLargeMessageSizeSetting _maxLargeMessageSize;
        private readonly MaxSmallMessageSizeSetting _maxSmallMessageSize;
        private readonly ICompressor _compressor;
        private readonly IDispatchContextManager _dispatchContextManager;
        private readonly ILargeMessageBodyStore _largeMessageBodyStore;
        private readonly ISerializer _serializer;
        private readonly ITypeProvider _typeProvider;
        private readonly IClock _clock;

        public BrokeredMessageFactory(MaxLargeMessageSizeSetting maxLargeMessageSize,
                                      MaxSmallMessageSizeSetting maxSmallMessageSize,
                                      IClock clock,
                                      ICompressor compressor,
                                      IDispatchContextManager dispatchContextManager,
                                      ILargeMessageBodyStore largeMessageBodyStore,
                                      ISerializer serializer,
                                      ITypeProvider typeProvider)
        {
            _serializer = serializer;
            _maxLargeMessageSize = maxLargeMessageSize;
            _maxSmallMessageSize = maxSmallMessageSize;
            _compressor = compressor;
            _dispatchContextManager = dispatchContextManager;
            _largeMessageBodyStore = largeMessageBodyStore;
            _typeProvider = typeProvider;
            _clock = clock;
        }

        public Task<BrokeredMessage> BuildBrokeredMessage(NimbusMessage message)
        {
            return Task.Run(async () =>
                                  {
                                      BrokeredMessage brokeredMessage;
                                      var messageBodyBytes = SerializeNimbusMessage(message);

                                      if (messageBodyBytes.Length > _maxLargeMessageSize)
                                      {
                                          var errorMessage =
                                              "Message body size of {0} is larger than the permitted maximum of {1}. You need to change this in your bus configuration settings if you want to send messages this large."
                                                  .FormatWith(messageBodyBytes.Length, _maxLargeMessageSize.Value);
                                          throw new BusException(errorMessage);
                                      }

                                      if (messageBodyBytes.Length > _maxSmallMessageSize)
                                      {
                                          brokeredMessage = new BrokeredMessage();
                                          var expiresAfter = message.ExpiresAfter;
                                          var blobIdentifier = await _largeMessageBodyStore.Store(message.MessageId, messageBodyBytes, expiresAfter);
                                          brokeredMessage.Properties[MessagePropertyKeys.LargeBodyBlobIdentifier] = blobIdentifier;
                                      }
                                      else
                                      {
                                          brokeredMessage = new BrokeredMessage(messageBodyBytes);
                                      }

                                      var currentDispatchContext = _dispatchContextManager.GetCurrentDispatchContext();
                                      brokeredMessage.MessageId = message.MessageId.ToString();
                                      brokeredMessage.CorrelationId = currentDispatchContext.CorrelationId.ToString();
                                      brokeredMessage.ReplyTo = message.From;
                                      brokeredMessage.TimeToLive = message.ExpiresAfter.Subtract(DateTimeOffset.UtcNow);
                                      brokeredMessage.ScheduledEnqueueTimeUtc = message.ScheduledEnqueueTimeUtc;
                                      brokeredMessage.Properties[MessagePropertyKeys.PrecedingMessageId] = currentDispatchContext.ResultOfMessageId;

                                      foreach (var property in message.Properties)
                                      {
                                          brokeredMessage.Properties.Add(property.Key, property.Value);
                                      }

                                      return brokeredMessage;
                                  });
        }

        public async Task<NimbusMessage> BuildNimbusMessage(BrokeredMessage message)
        {
            var nimbusMessage = await DeserializeNimbusMessage(message);
            return nimbusMessage;
        }

        private byte[] SerializeNimbusMessage(object serializableObject)
        {
            var serialized = _serializer.Serialize(serializableObject);
            var serializedBytes = Encoding.UTF8.GetBytes(serialized);
            var compressedBytes = _compressor.Compress(serializedBytes);
            return compressedBytes;
        }

        public async Task<NimbusMessage> DeserializeNimbusMessage(BrokeredMessage message)
        {
            byte[] bodyBytes;

            object blobId;
            if (message.Properties.TryGetValue(MessagePropertyKeys.LargeBodyBlobIdentifier, out blobId))
            {
                bodyBytes = await _largeMessageBodyStore.Retrieve((string) blobId);
            }
            else
            {
                // Yep, this will actually give us the body Stream instead of trying to deserialize the body... cool API bro!
                using (var dataStream = message.GetBody<Stream>())
                using (var memoryStream = new MemoryStream())
                {
                    dataStream.CopyTo(memoryStream);
                    bodyBytes = memoryStream.ToArray();
                }
            }

            var decompressedBytes = _compressor.Decompress(bodyBytes);
            var deserialized = (NimbusMessage) _serializer.Deserialize(Encoding.UTF8.GetString(decompressedBytes), typeof (NimbusMessage));
            return deserialized;
        }
    }
}