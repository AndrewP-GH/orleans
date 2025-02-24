
using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Orleans.CodeGeneration;
using Orleans.Serialization;

namespace Orleans.Runtime
{
    internal class MessageFactory
    {
        private readonly DeepCopier deepCopier;
        private readonly ILogger logger;
        private readonly MessagingTrace messagingTrace;

        public MessageFactory(DeepCopier deepCopier, ILogger<MessageFactory> logger, MessagingTrace messagingTrace)
        {
            this.deepCopier = deepCopier;
            this.logger = logger;
            this.messagingTrace = messagingTrace;
        }

        public Message CreateMessage(object body, InvokeMethodOptions options)
        {
            var (requestContextData, runningRequest) = RequestContextExtensions.ExportInternal(this.deepCopier);
            var callChainId = runningRequest switch
            {
                Message msg when msg.CallChainId != Guid.Empty => msg.CallChainId,
                _ => Guid.NewGuid(),
            };

            var message = new Message
            {
                Category = Message.Categories.Application,
                Direction = (options & InvokeMethodOptions.OneWay) != 0 ? Message.Directions.OneWay : Message.Directions.Request,
                Id = CorrelationId.GetNext(),
                IsReadOnly = (options & InvokeMethodOptions.ReadOnly) != 0,
                IsUnordered = (options & InvokeMethodOptions.Unordered) != 0,
                IsAlwaysInterleave = (options & InvokeMethodOptions.AlwaysInterleave) != 0,
                BodyObject = body,
                RequestContextData = requestContextData,
                CallChainId = callChainId,
            };

            messagingTrace.OnCreateMessage(message);
            return message;
        }

        public Message CreateResponseMessage(Message request)
        {
            var response = new Message
            {
                Category = request.Category,
                Direction = Message.Directions.Response,
                Id = request.Id,
                IsReadOnly = request.IsReadOnly,
                IsAlwaysInterleave = request.IsAlwaysInterleave,
                TargetSilo = request.SendingSilo,
                CallChainId = request.CallChainId,
            };

            if (!request.SendingGrain.IsDefault)
            {
                response.TargetGrain = request.SendingGrain;
            }

            response.SendingSilo = request.TargetSilo;
            if (!request.TargetGrain.IsDefault)
            {
                response.SendingGrain = request.TargetGrain;
            }

            response.CacheInvalidationHeader = request.CacheInvalidationHeader;
            response.TimeToLive = request.TimeToLive;

            var contextData = RequestContextExtensions.Export(this.deepCopier);
            if (contextData != null)
            {
                response.RequestContextData = contextData;
            }

            messagingTrace.OnCreateMessage(response);
            return response;
        }

        public Message CreateRejectionResponse(Message request, Message.RejectionTypes type, string info, Exception ex = null)
        {
            var response = this.CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Rejection;
            response.RejectionType = type;
            response.RejectionInfo = info;
            response.BodyObject = ex;
            if (this.logger.IsEnabled(LogLevel.Debug))
                this.logger.LogDebug(
                    ex,
                    "Creating {RejectionType} rejection with info '{Info}' at:" + Environment.NewLine + "{StackTrace}",
                    type,
                    info,
                    Utils.GetStackTrace());
            return response;
        }

        internal Message CreateDiagnosticResponseMessage(Message request, bool isExecuting, bool isWaiting, List<string> diagnostics)
        {
            var response = this.CreateResponseMessage(request);
            response.Result = Message.ResponseTypes.Status;
            response.BodyObject = new StatusResponse(isExecuting, isWaiting, diagnostics);

            if (this.logger.IsEnabled(LogLevel.Debug)) this.logger.LogDebug("Creating {RequestMessage} status update with diagnostics {Diagnostics}", request, diagnostics);

            return response;
        }
    }
}