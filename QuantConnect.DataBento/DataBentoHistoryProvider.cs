/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 */

using System.Collections.ObjectModel;
using NodaTime;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Logging;
using QuantConnect.Util;
using HistoryRequest = QuantConnect.Data.HistoryRequest;

namespace QuantConnect.DateBento
{
    public class DataBentoHistoryProvider : SynchronizingHistoryProvider, IDisposable
    {
        private static readonly ReadOnlyCollection<SecurityType> _supportedSecurityTypes = Array.AsReadOnly(new[]
        {
            SecurityType.Equity,
        });

        private readonly DataBentoApi _api;
        private readonly DataBentoSymbolMapper _symbolMapper = new();
        private readonly string _apiKey;
        private readonly int _publisherId;

        private int _dataPointCount;

        private bool _unsupportedSecurityTypeMessageLogged;
        private bool _unsupportedTickTypeMessagedLogged;
        private bool _unsupportedDataTypeMessageLogged;

        public override int DataPointCount => _dataPointCount;

        public DataBentoHistoryProvider(string apiKey,
            DataBentoApi.DataBentoPublishers publisher = DataBentoApi.DataBentoPublishers.XCIS) : this(
            apiKey, (int)publisher)
        {
        }

        public DataBentoHistoryProvider(string apiKey, int publisherId)
        {
            _apiKey = apiKey;
            _api = new DataBentoApi(apiKey);
            _publisherId = publisherId;
        }

        public DataBentoHistoryProvider() : this(
            Config.Get("databento-api-key"),
            Config.GetInt("databento-publisher-id", (int)DataBentoApi.DataBentoPublishers.XCIS))
        {
        }


        public override void Initialize(HistoryProviderInitializeParameters parameters)
        {
        }


        /// <summary>
        /// Gets the history for the requested security
        /// </summary>
        /// <param name="request">The historical data request</param>
        /// <returns>An enumerable of BaseData points</returns>
        public IEnumerable<BaseData> GetHistory(HistoryRequest request)
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new DataBentoAuthenticationException("History calls for DataBento.io require an API key.");
            }

            if (request.Symbol.IsCanonical() || !IsSupported(request.Symbol.SecurityType, request.DataType,
                    request.TickType, request.Resolution))
            {
                yield break;
            }

            // Quote data can only be fetched from databento from their Quote Tick endpoint,
            // which would be too slow for anything above second resolution or long time spans.
            if (request.TickType == TickType.Quote && request.Resolution > Resolution.Second)
            {
                Log.Error(
                    $"{nameof(DataBentoDataDownloader)}.{nameof(GetHistory)}(): Quote data above second resolution is not supported.");
                yield break;
            }

            if (request.TickType == TickType.Trade && request.Resolution > Resolution.Tick)
            {
                foreach (var data in GetAggregates(request))
                {
                    Interlocked.Increment(ref _dataPointCount);
                    yield return data;
                }
            }
            else if (request.TickType == TickType.Trade && request.Resolution == Resolution.Tick)
            {
                foreach (var data in GetTicks(request))
                {
                    Interlocked.Increment(ref _dataPointCount);
                    yield return data;
                }
            }
            else if (request.TickType == TickType.Quote)
            {
                IDataConsolidator consolidator = request.Resolution != Resolution.Tick
                    ? new TickQuoteBarConsolidator(request.Resolution.ToTimeSpan())
                    : FilteredIdentityDataConsolidator.ForTickType(request.TickType);

                var quotes = GetQuotes(request);
                BaseData? consolidatedData = null;
                DataConsolidatedHandler onDataConsolidated = (s, e) => { consolidatedData = (BaseData)e; };
                consolidator.DataConsolidated += onDataConsolidated;

                foreach (var data in quotes)
                {
                    consolidator.Update(data);
                    if (consolidatedData != null)
                    {
                        Interlocked.Increment(ref _dataPointCount);
                        yield return consolidatedData;
                        consolidatedData = null;
                    }
                }

                consolidator.DataConsolidated -= onDataConsolidated;
                consolidator.DisposeSafely();
            }
        }

        /// <summary>
        /// Gets the trade bars for the specified history request
        /// </summary>
        private IEnumerable<TradeBar> GetAggregates(HistoryRequest request)
        {
            var ticker = _symbolMapper.GetBrokerageSymbol(request.Symbol);
            var resolutionTimeSpan = request.Resolution.ToTimeSpan();

            var candles = _api.GetCandleData(ticker, request.Resolution, request.StartTimeUtc, request.EndTimeUtc,
                _publisherId);
            foreach (var candle in candles)
            {
                yield return new TradeBar(candle.Time, request.Symbol, candle.Open, candle.High, candle.Low,
                    candle.Close, candle.Volume, resolutionTimeSpan);
            }
        }

        private IEnumerable<Tick> GetQuotes(HistoryRequest request)
        {
            var ticker = _symbolMapper.GetBrokerageSymbol(request.Symbol);

            var quotes = _api.GetTBBOBookChanges(ticker, request.StartTimeUtc, request.EndTimeUtc, _publisherId);
            foreach (var quote in quotes)
            {
                //todo exchange
                yield return new Tick(quote.EventTimestamp, request.Symbol, string.Empty, Exchange.NYSE, quote.BidSize,
                    quote.BidPrice, quote.AskSize, quote.AskPrice);
            }
        }

        private IEnumerable<Tick> GetTicks(HistoryRequest request)
        {
            var ticker = _symbolMapper.GetBrokerageSymbol(request.Symbol);

            // todo what about the provider id here?
            var ticks = _api.GetTrades(ticker, request.StartTimeUtc, request.EndTimeUtc);
            foreach (var tick in ticks)
            {
                yield return new Tick()
                {
                    Time = tick.EventTimestamp, //Todo event timestamp or recv. timestamp
                    Symbol = request.Symbol,
                    Quantity = tick.Size,
                    Value = tick.Price,
                    TickType = request.TickType
                };
            }
        }

        public override IEnumerable<Slice> GetHistory(IEnumerable<HistoryRequest> requests, DateTimeZone sliceTimeZone)
        {
            var subscriptions = new List<Subscription>();
            foreach (var request in requests)
            {
                var history = GetHistory(request);
                var subscription = CreateSubscription(request, history);
                subscriptions.Add(subscription);
            }

            return CreateSliceEnumerableFromSubscriptions(subscriptions, sliceTimeZone);
        }


        private bool IsSupported(SecurityType securityType, Type dataType, TickType tickType, Resolution resolution)
        {
            // Check supported security types
            if (!IsSecurityTypeSupported(securityType))
            {
                if (!_unsupportedSecurityTypeMessageLogged)
                {
                    Log.Trace($"DataBentoDataProvider.IsSupported(): Unsupported security type: {securityType}");
                    _unsupportedSecurityTypeMessageLogged = true;
                }

                return false;
            }

            if (tickType == TickType.OpenInterest)
            {
                if (!_unsupportedTickTypeMessagedLogged)
                {
                    Log.Trace($"DataBentoDataProvider.IsSupported(): Unsupported tick type: {tickType}");
                    _unsupportedTickTypeMessagedLogged = true;
                }

                return false;
            }

            if (!dataType.IsAssignableFrom(typeof(TradeBar)) &&
                !dataType.IsAssignableFrom(typeof(QuoteBar)) &&
                !dataType.IsAssignableFrom(typeof(Tick)))
            {
                if (!_unsupportedDataTypeMessageLogged)
                {
                    Log.Trace($"DataBentoDataProvider.IsSupported(): Unsupported data type: {dataType}");
                    _unsupportedDataTypeMessageLogged = true;
                }

                return false;
            }


            return true;
        }

        /// <summary>
        /// Determines whether or not the specified security type is a supported option
        /// </summary>
        private static bool IsSecurityTypeSupported(SecurityType securityType)
        {
            return _supportedSecurityTypes.Contains(securityType);
        }

        public void Dispose()
        {
        }
    }
}