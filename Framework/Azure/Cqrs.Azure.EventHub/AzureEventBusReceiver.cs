﻿#region Copyright
// // -----------------------------------------------------------------------
// // <copyright company="Chinchilla Software Limited">
// // 	Copyright Chinchilla Software Limited. All rights reserved.
// // </copyright>
// // -----------------------------------------------------------------------
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using cdmdotnet.Logging;
using Cqrs.Authentication;
using Cqrs.Bus;
using Cqrs.Configuration;
using Cqrs.Events;
using Cqrs.Messages;
using Microsoft.ServiceBus.Messaging;
using EventData = Microsoft.ServiceBus.Messaging.EventData;

namespace Cqrs.Azure.ServiceBus
{
	/// <summary>
	/// A <see cref="IEventReceiver{TAuthenticationToken}"/> that receives network messages, resolves handlers and executes the handler.
	/// </summary>
	/// <typeparam name="TAuthenticationToken">The <see cref="Type"/> of the authentication token.</typeparam>
	public class AzureEventBusReceiver<TAuthenticationToken>
		: AzureEventHubBus<TAuthenticationToken>
		, IEventHandlerRegistrar
		, IEventReceiver<TAuthenticationToken>
	{
		/// <summary>
		/// The configuration key for
		/// the number of receiver <see cref="SubscriptionClient"/> instances to create
		/// as used by <see cref="IConfigurationManager"/>.
		/// </summary>
		protected virtual string NumberOfReceiversCountConfigurationKey
		{
			get { return "Cqrs.Azure.EventHub.EventBus.NumberOfReceiversCount"; }
		}

		// ReSharper disable StaticMemberInGenericType
		/// <summary>
		/// Gets the <see cref="RouteManager"/>.
		/// </summary>
		public static RouteManager Routes { get; private set; }

		/// <summary>
		/// The number of handles currently being executed.
		/// </summary>
		protected static long CurrentHandles { get; set; }
		// ReSharper restore StaticMemberInGenericType

		static AzureEventBusReceiver()
		{
			Routes = new RouteManager();
		}

		/// <summary>
		/// Instantiates a new instance of <see cref="AzureEventBusReceiver{TAuthenticationToken}"/>.
		/// </summary>
		public AzureEventBusReceiver(IConfigurationManager configurationManager, IMessageSerialiser<TAuthenticationToken> messageSerialiser, IAuthenticationTokenHelper<TAuthenticationToken> authenticationTokenHelper, ICorrelationIdHelper correlationIdHelper, ILogger logger, IAzureBusHelper<TAuthenticationToken> azureBusHelper)
			: base(configurationManager, messageSerialiser, authenticationTokenHelper, correlationIdHelper, logger, azureBusHelper, false)
		{
			TelemetryHelper = configurationManager.CreateTelemetryHelper("Cqrs.Azure.EventHub.EventBus.Receiver.UseApplicationInsightTelemetryHelper", correlationIdHelper);
		}

		/// <summary>
		/// Register an event handler that will listen and respond to events.
		/// </summary>
		/// <remarks>
		/// In many cases the <paramref name="targetedType"/> will be the handler class itself, what you actually want is the target of what is being updated.
		/// </remarks>
		public virtual void RegisterHandler<TMessage>(Action<TMessage> handler, Type targetedType, bool holdMessageLock = true)
			where TMessage : IMessage
		{
			AzureBusHelper.RegisterHandler(TelemetryHelper, Routes, handler, targetedType, holdMessageLock);
		}

		/// <summary>
		/// Register an event handler that will listen and respond to events.
		/// </summary>
		public void RegisterHandler<TMessage>(Action<TMessage> handler, bool holdMessageLock = false)
			where TMessage : IMessage
		{
			RegisterHandler(handler, null, holdMessageLock);
		}

		/// <summary>
		/// Register an event handler that will listen and respond to all events.
		/// </summary>
		public void RegisterGlobalEventHandler<TMessage>(Action<TMessage> handler, bool holdMessageLock = true)
			where TMessage : IMessage
		{
			Routes.RegisterGlobalEventHandler(handler, holdMessageLock);
		}

		/// <summary>
		/// Receives a <see cref="BrokeredMessage"/> from the event bus.
		/// </summary>
		protected virtual void ReceiveEvent(PartitionContext context, EventData eventData)
		{
			DateTimeOffset startedAt = DateTimeOffset.UtcNow;
			Stopwatch mainStopWatch = Stopwatch.StartNew();
			string responseCode = "200";
			// Null means it was skipped
			bool? wasSuccessfull = true;
			string telemetryName = string.Format("Cqrs/Handle/Event/{0}", eventData.SequenceNumber);
			ISingleSignOnToken authenticationToken = null;

			IDictionary<string, string> telemetryProperties = new Dictionary<string, string> { { "Type", "Azure/EventHub" } };
			object value;
			if (eventData.Properties.TryGetValue("Type", out value))
				telemetryProperties.Add("MessageType", value.ToString());
			TelemetryHelper.TrackMetric("Cqrs/Handle/Event", CurrentHandles++, telemetryProperties);
			// Do a manual 10 try attempt with back-off
			for (int i = 0; i < 10; i++)
			{
				try
				{
					Logger.LogDebug(string.Format("An event message arrived with the partition key '{0}', sequence number '{1}' and offset '{2}'.", eventData.PartitionKey, eventData.SequenceNumber, eventData.Offset));
					string messageBody = Encoding.UTF8.GetString(eventData.GetBytes());

					IEvent<TAuthenticationToken> @event = AzureBusHelper.ReceiveEvent(messageBody, ReceiveEvent,
						string.Format("partition key '{0}', sequence number '{1}' and offset '{2}'", eventData.PartitionKey, eventData.SequenceNumber, eventData.Offset),
						() =>
						{
							wasSuccessfull = null;
							telemetryName = string.Format("Cqrs/Handle/Event/Skipped/{0}", eventData.SequenceNumber);
							responseCode = "204";
							// Remove message from queue
							context.CheckpointAsync(eventData);
							Logger.LogDebug(string.Format("An event message arrived with the partition key '{0}', sequence number '{1}' and offset '{2}' but processing was skipped due to event settings.", eventData.PartitionKey, eventData.SequenceNumber, eventData.Offset));
							TelemetryHelper.TrackEvent("Cqrs/Handle/Event/Skipped", telemetryProperties);
						}
					);

					if (wasSuccessfull != null)
					{
						if (@event != null)
						{
							telemetryName = string.Format("{0}/{1}/{2}", @event.GetType().FullName, @event.GetIdentity(), @event.Id);
							authenticationToken = @event.AuthenticationToken as ISingleSignOnToken;

							var telemeteredMessage = @event as ITelemeteredMessage;
							if (telemeteredMessage != null)
								telemetryName = telemeteredMessage.TelemetryName;

							telemetryName = string.Format("Cqrs/Handle/Event/{0}", telemetryName);
						}
						// Remove message from queue
						context.CheckpointAsync(eventData);
					}
					Logger.LogDebug(string.Format("An event message arrived and was processed with the partition key '{0}', sequence number '{1}' and offset '{2}'.", eventData.PartitionKey, eventData.SequenceNumber, eventData.Offset));

					IList<IEvent<TAuthenticationToken>> events;
					if (EventWaits.TryGetValue(@event.CorrelationId, out events))
						events.Add(@event);

					wasSuccessfull = true;
					responseCode = "200";
					return;
				}
				catch (Exception exception)
				{
					// Indicates a problem, unlock message in queue
					Logger.LogError(string.Format("An event message arrived with the partition key '{0}', sequence number '{1}' and offset '{2}' but failed to be process.", eventData.PartitionKey, eventData.SequenceNumber, eventData.Offset), exception: exception);

					switch (i)
					{
						case 0:
						case 1:
							// 10 seconds
							Thread.Sleep(10 * 1000);
							break;
						case 2:
						case 3:
							// 30 seconds
							Thread.Sleep(30 * 1000);
							break;
						case 4:
						case 5:
						case 6:
							// 1 minute
							Thread.Sleep(60 * 1000);
							break;
						case 7:
						case 8:
							// 3 minutes
							Thread.Sleep(3 * 60 * 1000);
							break;
						case 9:
							telemetryProperties.Add("ExceptionType", exception.GetType().FullName);
							telemetryProperties.Add("ExceptionMessage", exception.Message);
							break;
					}
					wasSuccessfull = false;
					responseCode = "500";
				}
				finally
				{
					// Eventually just accept it
					context.CheckpointAsync(eventData);

					TelemetryHelper.TrackMetric("Cqrs/Handle/Event", CurrentHandles--, telemetryProperties);

					mainStopWatch.Stop();
					TelemetryHelper.TrackRequest
					(
						telemetryName,
						authenticationToken,
						startedAt,
						mainStopWatch.Elapsed,
						responseCode,
						wasSuccessfull == null || wasSuccessfull.Value,
						telemetryProperties
					);

					TelemetryHelper.Flush();
				}
			}
		}

		/// <summary>
		/// Receives a <see cref="IEvent{TAuthenticationToken}"/> from the event bus.
		/// </summary>
		public virtual bool? ReceiveEvent(IEvent<TAuthenticationToken> @event)
		{
			return AzureBusHelper.DefaultReceiveEvent(@event, Routes, "Azure-EventHub");
		}

		#region Overrides of AzureServiceBus<TAuthenticationToken>

		/// <summary>
		/// Returns <see cref="NumberOfReceiversCountConfigurationKey"/> from <see cref="IConfigurationManager"/> 
		/// if no value is set, returns <see cref="AzureBus{TAuthenticationToken}.DefaultNumberOfReceiversCount"/>.
		/// </summary>
		protected override int GetCurrentNumberOfReceiversCount()
		{
			string numberOfReceiversCountValue;
			int numberOfReceiversCount;
			if (ConfigurationManager.TryGetSetting(NumberOfReceiversCountConfigurationKey, out numberOfReceiversCountValue) && !string.IsNullOrWhiteSpace(numberOfReceiversCountValue))
			{
				if (!int.TryParse(numberOfReceiversCountValue, out numberOfReceiversCount))
					numberOfReceiversCount = DefaultNumberOfReceiversCount;
			}
			else
				numberOfReceiversCount = DefaultNumberOfReceiversCount;
			return numberOfReceiversCount;
		}

		#endregion

		#region Implementation of IEventReceiver

		/// <summary>
		/// Starts listening and processing instances of <see cref="IEvent{TAuthenticationToken}"/> from the event bus.
		/// </summary>
		public void Start()
		{
			InstantiateReceiving();

			// Callback to handle received messages
			RegisterReceiverMessageHandler(ReceiveEvent);
		}

		#endregion
	}
}