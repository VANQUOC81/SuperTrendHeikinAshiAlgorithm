#region imports
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Globalization;
    using System.Drawing;
    using QuantConnect;
    using QuantConnect.Algorithm.Framework;
    using QuantConnect.Algorithm.Framework.Selection;
    using QuantConnect.Algorithm.Framework.Alphas;
    using QuantConnect.Algorithm.Framework.Portfolio;
    using QuantConnect.Algorithm.Framework.Execution;
    using QuantConnect.Algorithm.Framework.Risk;
    using QuantConnect.Parameters;
    using QuantConnect.Benchmarks;
    using QuantConnect.Brokerages;
    using QuantConnect.Util;
    using QuantConnect.Interfaces;
    using QuantConnect.Algorithm;
    using QuantConnect.Indicators;
    using QuantConnect.Data;
    using QuantConnect.Data.Consolidators;
    using QuantConnect.Data.Custom;
    using QuantConnect.DataSource;
    using QuantConnect.Data.Fundamental;
    using QuantConnect.Data.Market;
    using QuantConnect.Data.UniverseSelection;
    using QuantConnect.Notifications;
    using QuantConnect.Orders;
    using QuantConnect.Orders.Fees;
    using QuantConnect.Orders.Fills;
    using QuantConnect.Orders.Slippage;
    using QuantConnect.Scheduling;
    using QuantConnect.Securities;
    using QuantConnect.Securities.Equity;
    using QuantConnect.Securities.Future;
    using QuantConnect.Securities.Option;
    using QuantConnect.Securities.Forex;
    using QuantConnect.Securities.Crypto;   
    using QuantConnect.Securities.Interfaces;
    using QuantConnect.Storage;
    using QuantConnect.Data.Custom.AlphaStreams;
    using QCAlgorithmFramework = QuantConnect.Algorithm.QCAlgorithm;
    using QCAlgorithmFrameworkBridge = QuantConnect.Algorithm.QCAlgorithm;
#endregion
namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Super Trend Heikin Ashi Algorithm EUR/USD 
    //  Scalp Rules:
    /// - use 5 minute resolution consolidator
    /// </summary>
    /// <remarks>
    /// QC Time is always NY UTC-4 timezone. Beware when comparing with TradingView
    /// </remarks>
    public class SuperTrendHeikinAshiAlgorithm : QCAlgorithm
    {
        private Symbol _eurusd;

        private HeikinAshi _heikinashi;

        private SuperTrend _superTrend;

        private const DataNormalizationMode _dataNormalizationMode = DataNormalizationMode.Raw;

        private IDataConsolidator _fiveMinuteConsolidator;

        private decimal _lotSize;

        private bool _directionChanged = false;

        private string trendDirection = string.Empty;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetStartDate(2023, 4, 16);  //Set Start Date
            SetEndDate(2023, 5, 9);    //Set End Date
            SetCash(12000);             //Set Strategy Cash
            
            // Find more symbols here: http://quantconnect.com/data
            _eurusd = AddForex("EURUSD", Resolution.Minute, Market.Oanda).Symbol;
            SetBenchmark("EURUSD");
            SetBrokerageModel(BrokerageName.OandaBrokerage, AccountType.Margin);
            //SetBrokerageModel(BrokerageName.QuantConnectBrokerage);

            Securities[_eurusd].SetSlippageModel(new ConstantSlippageModel(0.0m));

            // TODO: filling prices which is way off compared to TradingView Backtest
            // tradeview fills at $1.10361 but QC fills at $1.10347 gap of 2.6 pips instead 1.5 normal spread
            // trade exit also horrible $1.10426 - $1.10447 = 2.1 pips instead of the 1.5 normal spread
            // investigate different or use custom fill model. See docs QC
            // below link states that QC uses L1 Spreads which mimics reality
            // https://www.quantconnect.com/forum/discussion/9451/difference-between-closing-and-fill-price/p1
            Securities[_eurusd].SetFillModel(new CustomFillModel(this));

            // Lot size:
            _lotSize = Securities[_eurusd].SymbolProperties.LotSize;
            
            // Print the lot size:
            Debug("The lot size is " + _lotSize);

            // Create a 5-minute consolidator
            _fiveMinuteConsolidator = new QuoteBarConsolidator(TimeSpan.FromMinutes(5));

            // Register the consolidator for "EURUSD" symbol for manual indicator updates
            // Use RegisterIndicator for automatic indicator updates
            SubscriptionManager.AddConsolidator(_eurusd, _fiveMinuteConsolidator);
            _fiveMinuteConsolidator.DataConsolidated += OnDataConsolidated;

            // Add indicators
            _heikinashi = new HeikinAshi();
            // MA Wilders most similar to tradingview SuperTrend calculated values
            //_superTrend = new SuperTrend(10, 3, MovingAverageType.Wilders);
            _superTrend = new SuperTrend(21, 1, MovingAverageType.Wilders);

            // Setup Warmup
            SetWarmUp(100, Resolution.Minute);
        }

        public void OnDataConsolidated(object sender, IBaseData consolidated)
        {
                // Update the Heikin Ashi indicator with the consolidated bar
                _heikinashi.Update(consolidated);

                // Access the Heikin Ashi values
                decimal haOpen = _heikinashi.Open;
                decimal haHigh = _heikinashi.High;
                decimal haLow = _heikinashi.Low;
                decimal haClose = _heikinashi.Close;

                var tradeBar = new TradeBar();
                tradeBar.Open = haOpen;
                tradeBar.High = haHigh;
                tradeBar.Low = haLow;
                tradeBar.Close = haClose;
                tradeBar.EndTime = Time;
                tradeBar.Period = TimeSpan.FromMinutes(5);
                tradeBar.Symbol = _eurusd;
                tradeBar.Value = haClose;

                // Update the SuperTrend indicator with Heikin Ashi values
                _superTrend.Update(tradeBar);

                // Perform your desired logic with the 5-minute consolidated bar data
                if (_heikinashi.IsReady && _superTrend.IsReady && !IsWarmingUp)
                {
                    DateTime ActualTimeStampOfSliceDataInformation = GetActualTimeStampOfSliceDataInformation(Time, Resolution.Minute, -5);

                    // Determine trend logic
                    var superTrendValue = RoundOffFiveDecimals(_superTrend.Current.Value);
                    var haCloseValue = RoundOffFiveDecimals(_heikinashi.Close.Current.Value);
                    
                    if(superTrendValue > haCloseValue)
                    {
                        if(trendDirection == "UPTREND")
                        {
                            _directionChanged = true;

                            Log($"{_eurusd.Value} : {Get24HoursFormat(ActualTimeStampOfSliceDataInformation)} Trend Direction Changed to DOWNTREND");
                        } 
                        else
                        {
                            _directionChanged = false;
                        }

                        // downtrend
                        trendDirection = "DOWNTREND";  
                    }
                    if(superTrendValue < haCloseValue)
                    {
                        if(trendDirection == "DOWNTREND")
                        {
                            _directionChanged = true;

                            Log($"{_eurusd.Value} : {Get24HoursFormat(ActualTimeStampOfSliceDataInformation)} Trend Direction Changed to UPTREND");
                        }
                        else
                        {
                            _directionChanged = false;
                        }

                        // uptrend
                        trendDirection = "UPTREND";

                    }

                    string colorHA = GetColorHa(_heikinashi.Close, _heikinashi.Open);
                    Log($@"{_eurusd.Value} : 
                    {Get24HoursFormat(ActualTimeStampOfSliceDataInformation)} {colorHA} HA 
                    O{RoundOffFiveDecimals(_heikinashi.Open.Current.Value)} H{RoundOffFiveDecimals(_heikinashi.High.Current.Value)} L{RoundOffFiveDecimals(_heikinashi.Low.Current.Value)} C{haCloseValue} 
                    SuperTrend Value {superTrendValue}
                    Trend Direction {trendDirection}"
                    );

                    if (_directionChanged && trendDirection == "UPTREND")
                    {
                        SetHoldings(_eurusd, 0.83, liquidateExistingHoldings: true);  
                        Log($"{_eurusd.Value} : {Get24HoursFormat(ActualTimeStampOfSliceDataInformation)} BUY");                     
                    }
                    else if (_directionChanged && trendDirection == "DOWNTREND")
                    {
                        SetHoldings(_eurusd, -0.83, liquidateExistingHoldings: true);
                        Log($"{_eurusd.Value} : {Get24HoursFormat(ActualTimeStampOfSliceDataInformation)} SELL");   
                    }
                }
        }

        public Decimal RoundOffFiveDecimals(Decimal value)
        {
            return Math.Round(value, 5);
        }

        public DateTime GetActualTimeStampOfSliceDataInformation(DateTime TimeStampFrontier, Resolution resolution, int minutes)
        {
            if(resolution == Resolution.Minute)
            {
                return TimeStampFrontier.AddMinutes(minutes);
            }

            return TimeStampFrontier;
        }

        public string Get24HoursFormat(DateTime dateTime)
        {
            return dateTime.ToString("HH:mm:ss");
        }

        public string GetColorHa(IndicatorBase<IndicatorDataPoint> close, IndicatorBase<IndicatorDataPoint> open)
        {
            if (close.Current.Value == open.Current.Value) return "WHITE";
            
            if (close.Current.Value > open.Current.Value)
            {
                //Log($" GREEN HA C{close.Current.Value} > O{open.Current.Value}");
                return "GREEN";
            }
            else
            {
                //Log($" RED HA C{close.Current.Value} < O{open.Current.Value}"); 
                return  "RED";
            }

            return null; 
        }
    }

    public class CustomFillModel : ImmediateFillModel
        {
            private readonly QCAlgorithm _algorithm;
            private readonly Random _random = new Random(387510346); // seed it for reproducibility
            private readonly Dictionary<long, decimal> _absoluteRemainingByOrderId = new Dictionary<long, decimal>();

            public CustomFillModel(QCAlgorithm algorithm)
            {
                _algorithm = algorithm;
            }

            public override OrderEvent MarketFill(Security asset, MarketOrder order)
            {
                // this model randomly fills market orders
                decimal absoluteRemaining;
                if (!_absoluteRemainingByOrderId.TryGetValue(order.Id, out absoluteRemaining))
                {
                    absoluteRemaining = order.AbsoluteQuantity;
                    _absoluteRemainingByOrderId.Add(order.Id, order.AbsoluteQuantity);
                }

                var fill = base.MarketFill(asset, order);
                var absoluteFillQuantity = (int) (Math.Min(absoluteRemaining, _random.Next(0, 2*(int)order.AbsoluteQuantity)));
                fill.FillQuantity = Math.Sign(order.Quantity) * absoluteFillQuantity;

                if (absoluteRemaining == absoluteFillQuantity)
                {
                    fill.Status = OrderStatus.Filled;
                    _absoluteRemainingByOrderId.Remove(order.Id);
                }
                else
                {
                    absoluteRemaining = absoluteRemaining - absoluteFillQuantity;
                    _absoluteRemainingByOrderId[order.Id] = absoluteRemaining;
                    fill.Status = OrderStatus.PartiallyFilled;
                }

                _algorithm.Log($"CustomFillModel: {fill}");

                return fill;
            }
        }
}
