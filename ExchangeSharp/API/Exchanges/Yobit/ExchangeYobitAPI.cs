﻿/*
MIT LICENSE

Copyright 2017 Digital Ruby, LLC - http://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;

namespace ExchangeSharp
{
    public sealed class ExchangeYobitAPI : ExchangeAPI
    {
        public override string Name => ExchangeName.Yobit;
        public override string BaseUrl { get; set; } = "https://yobit.net/api/3";
        public string PrivateURL { get; set; } = "https://yobit.net/tapi";

        static ExchangeYobitAPI()
        {
            ExchangeGlobalCurrencyReplacements[typeof(ExchangeYobitAPI)] = new KeyValuePair<string, string>[]
            {
                new KeyValuePair<string, string>("BCC", "BCH")
            };
        }

        public ExchangeYobitAPI()
        {
            RequestContentType = "application/x-www-form-urlencoded";

            // yobit is not easy to use - you must maintain the nonce in a file and keep incrementing and make new keys when it hits long.MaxValue
            // to add insult to injury you must always increment by exactly one from the last use of your API key, even when rebooting the computer and restarting your process
            NonceStyle = NonceStyle.Int32File;

            SymbolSeparator = "_";
            SymbolIsUppercase = false;
        }

        #region ProcessRequest 

        protected override async Task ProcessRequestAsync(HttpWebRequest request, Dictionary<string, object> payload)
        {
            // Only Private APIs are POST and need Authorization
            if (CanMakeAuthenticatedRequest(payload) && request.Method == "POST")
            {
                var msg = CryptoUtility.GetFormForPayload(payload);
                var sig = CryptoUtility.SHA512Sign(msg, PrivateApiKey.ToUnsecureString());
                request.Headers.Add("Key", PublicApiKey.ToUnsecureString());
                request.Headers.Add("Sign", sig.ToLower());

                using (Stream stream = await request.GetRequestStreamAsync())
                {
                    byte[] content = msg.ToBytesUTF8();
                    stream.Write(content, 0, content.Length);
                }
            }
        }

        #endregion

        #region Public APIs

        protected override Task<IReadOnlyDictionary<string, ExchangeCurrency>> OnGetCurrenciesAsync()
        {
            throw new NotSupportedException("Yobit does not provide data about its currencies via the API");
        }

        protected override async Task<IEnumerable<string>> OnGetSymbolsAsync()
        {
            List<string> symbols = new List<string>();
            JToken token = await MakeJsonRequestAsync<JToken>("/info", BaseUrl, null);
            foreach (JProperty prop in token["pairs"]) symbols.Add(prop.Name);
            return symbols;
        }

        protected override async Task<IEnumerable<ExchangeMarket>> OnGetSymbolsMetadataAsync()
        {
            List<ExchangeMarket> markets = new List<ExchangeMarket>();
            // "pairs":{"ltc_btc":{"decimal_places":8,"min_price":0.00000001,"max_price":10000,"min_amount":0.0001,"hidden":0,"fee":0.2} ... }
            JToken token = await MakeJsonRequestAsync<JToken>("/info", BaseUrl, null);
            foreach (JProperty prop in token["pairs"])
            {
                var split = prop.Name.ToUpper().Split('_');
                markets.Add(new ExchangeMarket()
                {
                    MarketName = prop.Name.ToStringInvariant(),
                    MarketCurrency = split[0],
                    BaseCurrency = split[1],
                    IsActive = prop.First["hidden"].ConvertInvariant<int>().Equals(0),
                    MaxPrice = prop.First["max_price"].ConvertInvariant<decimal>(),
                    MinPrice = prop.First["min_price"].ConvertInvariant<decimal>(),
                    MinTradeSize = prop.First["min_amount"].ConvertInvariant<decimal>()
                });
            }
            return markets;
        }

        protected override async Task<ExchangeTicker> OnGetTickerAsync(string symbol)
        {
            JToken token = await MakeJsonRequestAsync<JToken>("/ticker/" + NormalizeSymbol(symbol), null, null, "POST");
            if (token != null && token.HasValues) return ParseTicker(token.First as JProperty);
            return null;
        }

        /// <summary>
        /// WARNING: Yobit has over 7500 trading pairs, and this call could take over an hour to complete.
        /// Consider using GetTicker with symbol as a workable alternative.
        /// </summary>
        /// <returns></returns>
        protected override async Task<IEnumerable<KeyValuePair<string, ExchangeTicker>>> OnGetTickersAsync()
        {
            // we could return all tickers with a single call by retrieving all trading pairs and passing them as a url parameter...
            // On Yobit, however, this is over 7500 pairs, and we would have to break up the call (the url would be too long). So we'll use the base call and get them one at a time.
            return await base.OnGetTickersAsync();
        }

        protected override async Task<ExchangeOrderBook> OnGetOrderBookAsync(string symbol, int maxCount = 100)
        {
            symbol = NormalizeSymbol(symbol);
            JToken token = await MakeJsonRequestAsync<JToken>("/depth/" + symbol + "?limit=" + maxCount, BaseUrl, null);
            return ExchangeAPIExtensions.ParseOrderBookFromJTokenArrays(token[symbol]);
        }

        protected override async Task<IEnumerable<ExchangeTrade>> OnGetRecentTradesAsync(string symbol)
        {
            symbol = NormalizeSymbol(symbol);
            List<ExchangeTrade> trades = new List<ExchangeTrade>();
            JToken token = await MakeJsonRequestAsync<JToken>("/trades/" + symbol + "?limit=10", null, null, "POST");    // default is 150, max: 2000, let's do another arbitrary 10 for consistency
            foreach (JToken prop in token.First.First) trades.Add(ParseTrade(prop));
            return trades;
        }

        protected override async Task OnGetHistoricalTradesAsync(Func<IEnumerable<ExchangeTrade>, bool> callback, string symbol, DateTime? startDate = null, DateTime? endDate = null)
        {
            symbol = NormalizeSymbol(symbol);
            List<ExchangeTrade> trades = new List<ExchangeTrade>();
            // Not directly supported, but we'll return the max and filter if necessary
            JToken token = await MakeJsonRequestAsync<JToken>("/trades/" + symbol + "?limit=2000", null, null, "POST");
            token = token.First.First;      // bunch of nested 
            foreach (JToken prop in token)
            {
                ExchangeTrade trade = ParseTrade(prop);
                if (startDate != null) { if (trade.Timestamp >= startDate) trades.Add(trade); }
                else trades.Add(trade);
            }
            var rc = callback?.Invoke(trades);
        }

        /// <summary>
        /// Yobit doesn't support GetCandles. It is possible to get all trades since startdate (filter by enddate if needed) and then aggregate into MarketCandles by periodSeconds 
        /// TODO: Aggregate Yobit Trades into Candles. This may not be worth the effort because the max we can retrieve is 2000 which may or may not be out of the range of start and end for aggregate
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="periodSeconds"></param>
        /// <param name="startDate"></param>
        /// <param name="endDate"></param>
        /// <param name="limit"></param>
        /// <returns></returns>
        protected override Task<IEnumerable<MarketCandle>> OnGetCandlesAsync(string symbol, int periodSeconds, DateTime? startDate = null, DateTime? endDate = null, int? limit = null)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Private APIs

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "getInfo");
            // "return":{"funds":{"ltc":22,"nvc":423.998,"ppc":10,...},	"funds_incl_orders":{"ltc":32,"nvc":523.998,"ppc":20,...},"rights":{"info":1,"trade":0,"withdraw":0},"transaction_count":0,"open_orders":1,"server_time":1418654530}
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            foreach (JProperty prop in token["funds"])
            {
                var amount = prop.Value.ConvertInvariant<decimal>();
                if (amount > 0m)
                {
                    amounts.Add(prop.Name, amount);
                }
            }
            return amounts;
        }

        protected override async Task<Dictionary<string, decimal>> OnGetAmountsAvailableToTradeAsync()
        {
            Dictionary<string, decimal> amounts = new Dictionary<string, decimal>();
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "getInfo");
            // "return":{"funds":{"ltc":22,"nvc":423.998,"ppc":10,...},	"funds_incl_orders":{"ltc":32,"nvc":523.998,"ppc":20,...},"rights":{"info":1,"trade":0,"withdraw":0},"transaction_count":0,"open_orders":1,"server_time":1418654530}
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            foreach (JProperty prop in token["funds_incl_orders"])
            {
                var amount = prop.Value.ConvertInvariant<decimal>();
                if (amount > 0m)
                {
                    amounts.Add(prop.Name, amount);
                }
            }
            return amounts;
        }

        protected override async Task<ExchangeOrderResult> OnGetOrderDetailsAsync(string orderId, string symbol = null)
        {
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "getInfo");
            payload.Add("order_id", orderId);
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            return ParseOrder(token.First as JProperty);
        }


        /// <summary>
        /// Warning: Yobit will not return transactions over a week old via their api
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="afterDate"></param>
        /// <returns></returns>
        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetCompletedOrderDetailsAsync(string symbol = null, DateTime? afterDate = null)
        {
            if (symbol == null) { throw new APIException("symbol cannot be null"); } // Seriously, they want you to loop through over 7500 symbol pairs to find your trades! Geez...

            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();

            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "TradeHistory");
            payload.Add("pair", symbol);
            if (afterDate != null) payload.Add("since", new DateTimeOffset((DateTime)afterDate).ToUnixTimeSeconds());
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            if (token != null) foreach (JProperty prop in token) orders.Add(ParseOrder(prop));
            return orders;
        }

        protected override async Task<IEnumerable<ExchangeOrderResult>> OnGetOpenOrderDetailsAsync(string symbol = null)
        {
            if (symbol == null) { throw new APIException("symbol cannot be null"); } // Seriously, they want you to loop through over 7500 symbol pairs to find your trades! Geez...

            List<ExchangeOrderResult> orders = new List<ExchangeOrderResult>();
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "ActiveOrders");
            payload.Add("pair", symbol);
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            if (token != null) foreach (JProperty prop in token) orders.Add(ParseOrder(prop));
            foreach (JProperty prop in token) orders.Add(ParseOrder(prop));
            return orders;
        }

        protected override async Task<ExchangeOrderResult> OnPlaceOrderAsync(ExchangeOrderRequest order)
        {
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "Trade");
            payload.Add("pair", order.Symbol);
            payload.Add("type", order.IsBuy ? "buy" : "sell");
            payload.Add("rate", order.Price);
            payload.Add("amount", order.Amount);
            order.ExtraParameters.CopyTo(payload);

            // "return":{"received":0.1,"remains":0,"order_id":12345,"funds":{"btc":15,"ltc":51.82,	"nvc":0, ... }}
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            ExchangeOrderResult result = new ExchangeOrderResult()
            {
                OrderId = token["order_id"].ToStringInvariant(),
                OrderDate = DateTime.UtcNow,                        // since they don't pass it back
                AmountFilled = token["received"].ConvertInvariant<decimal>(),
            };

            result.Amount = token["remains"].ConvertInvariant<decimal>() + result.AmountFilled;
            if (result.Amount == result.AmountFilled) result.Result = ExchangeAPIOrderResult.Filled;
            else if (result.AmountFilled == 0m) result.Result = ExchangeAPIOrderResult.Pending;
            else result.Result = ExchangeAPIOrderResult.FilledPartially;

            return result;
        }

        protected override async Task OnCancelOrderAsync(string orderId, string symbol = null)
        {
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "CancelOrder");
            payload.Add("order_id", orderId);
            await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
        }

        protected override Task<IEnumerable<ExchangeTransaction>> OnGetDepositHistoryAsync(string symbol)
        {
            throw new NotImplementedException("Yobit does not provide a deposit history via the API");  // I don't wonder why
        }

        protected override async Task<ExchangeDepositDetails> OnGetDepositAddressAsync(string symbol, bool forceRegenerate = false)
        {
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("need_new", forceRegenerate ? 1 : 0);
            payload.Add("method", "GetDepositAddress");
            payload.Add("coinName", symbol);
            // "return":{"address": 1UHAnAWvxDB9XXETsi7z483zRRBmcUZxb3,"processed_amount": 1.00000000,"server_time": 1437146228 }
            JToken token = await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            return new ExchangeDepositDetails()
            {
                Address = token["address"].ToStringInvariant(),
                Symbol = symbol
            };
        }

        /// <summary>
        /// Warning: Use with discretion
        /// <rant> Yobit trading seems fine, their API is stable, but their deposits/withdraws are *VERY* problematic. 
        /// I'm being kind. Waited as much as two-weeks for deposts to show up on Exchange, even though they were confirmed on the blockchain. 
        /// </rant>
        /// </summary>
        /// <param name="withdrawalRequest"></param>
        /// <returns></returns>
        protected override async Task<ExchangeWithdrawalResponse> OnWithdrawAsync(ExchangeWithdrawalRequest withdrawalRequest)
        {
            ExchangeWithdrawalResponse response = new ExchangeWithdrawalResponse { Success = false };
            var payload = await OnGetNoncePayloadAsync();
            payload.Add("method", "WithdrawCoinsToAddress");
            payload.Add("coinName", withdrawalRequest.Symbol);
            payload.Add("amount", withdrawalRequest.Amount);
            payload.Add("address", withdrawalRequest.Address);
            await MakeJsonRequestAsync<JToken>("/", PrivateURL, payload, "POST");
            response.Success = true;
            return response;
        }


        #endregion

        #region Private Functions

        private ExchangeTicker ParseTicker(JProperty prop)
        {
            var split = prop.Name.ToUpper().Split('_');
            if (split.Length != 2)
            {
                split = new string[2];
            }

            // "ltc_btc":{ "high":105.41,"low":104.67,"avg":105.04,"vol":43398.22251455,"vol_cur":4546.26962359,"last":105.11,"buy":104.2,"sell":105.11,"updated":1418654531 }
            return new ExchangeTicker
            {
                Ask = prop.First["sell"].ConvertInvariant<decimal>(),
                Bid = prop.First["buy"].ConvertInvariant<decimal>(),
                Last = prop.First["last"].ConvertInvariant<decimal>(),
                Volume = new ExchangeVolume
                {
                    ConvertedVolume = prop.First["vol_cur"].ConvertInvariant<decimal>(),
                    BaseVolume = prop.First["vol"].ConvertInvariant<decimal>(),
                    ConvertedSymbol = split[0],
                    BaseSymbol = split[1],
                    Timestamp = DateTimeOffset.FromUnixTimeSeconds(prop.First["updated"].ConvertInvariant<long>()).DateTime
                }
            };
        }

        private ExchangeTrade ParseTrade(JToken prop)
        {
            // "ltc_btc":[{"type":"ask","price":104.2,"amount":0.101,"tid":41234426,"timestamp":1418654531}, ... ]
            return new ExchangeTrade()
            {
                Amount = prop["amount"].ConvertInvariant<decimal>(),
                Id = prop["tid"].ConvertInvariant<long>(),
                Price = prop["price"].ConvertInvariant<decimal>(),
                IsBuy = prop["type"].ToStringInvariant().Equals("ask"),
                Timestamp = DateTimeOffset.FromUnixTimeSeconds(prop["timestamp"].ConvertInvariant<long>()).DateTime
            };
        }

        private ExchangeOrderResult ParseOrder(JProperty prop)
        {
            // "return":{"100025362":{"pair":ltc_btc,"type":sell,"start_amount":13.345,"amount":12.345,"rate":485,"timestamp_created":1418654530,"status":0 }
            // status is legacy and not used
            ExchangeOrderResult result = new ExchangeOrderResult()
            {
                OrderId = prop.Name,
                Symbol = prop["pair"].ToStringInvariant(),
                Amount = prop["start_amount"].ConvertInvariant<decimal>(),
                AmountFilled = prop["amount"].ConvertInvariant<decimal>(),
                Price = prop["rate"].ConvertInvariant<decimal>(),
                OrderDate = DateTimeOffset.FromUnixTimeSeconds(prop.First["timestamp_created"].ConvertInvariant<long>()).DateTime
            };

            if (result.Amount == result.AmountFilled) result.Result = ExchangeAPIOrderResult.Filled;
            else if (result.AmountFilled == 0m) result.Result = ExchangeAPIOrderResult.Pending;
            else result.Result = ExchangeAPIOrderResult.FilledPartially;

            return result;
        }

        #endregion
    }
}
