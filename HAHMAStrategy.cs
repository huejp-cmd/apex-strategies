// =============================================================================
//  HAHMAStrategy.cs — Stratégie NinjaTrader 8
//  Auteur     : JP (configurée par OpenClaw)
//  Timeframe  : Recommandé 10M–15M
//  Instrument : ETH Futures (MET / ETH CME) — ou tout futures CME
//  Version    : 2.0 — Avril 2026
//
//  ┌─────────────────────────────────────────────────────────────────────┐
//  │  LOGIQUE SHORT (Vente)                                              │
//  │  ─────────────────────                                              │
//  │  Signal  : Candle CLASSIQUE ROUGE + HA ROUGE + HA Close < HMA(50)  │
//  │  Entrée  : Close de la bougie signal                                │
//  │  SL      : High de la bougie HA ROUGE (signal)                     │
//  │  TP      : HA donne le signal (vert + HMA20) → sortie sur close    │
//  │            de la bougie CLASSIQUE du même bar                       │
//  ├─────────────────────────────────────────────────────────────────────┤
//  │  LOGIQUE LONG (Achat) — candle CLASSIQUE uniquement, pas HA        │
//  │  ─────────────────────────────────────────────────────────────────  │
//  │  Signal  : Candle CLASSIQUE VERTE + Classic Close > HMA(50)        │
//  │  Entrée  : Close de la bougie classique signal                      │
//  │  SL      : Low de la bougie CLASSIQUE signal                       │
//  │  TP      : HA donne le signal (rouge + HMA20) → sortie sur close   │
//  │            de la bougie CLASSIQUE du même bar                       │
//  ├─────────────────────────────────────────────────────────────────────┤
//  │  GESTION DU CAPITAL                                                 │
//  │  Capital initial : $50 000 USD                                      │
//  │  Mise par trade  : 2% du capital courant (compounding auto)         │
//  │  Contrats calculés : Risque$ / (Distance SL × Valeur du point)     │
//  └─────────────────────────────────────────────────────────────────────┘
// =============================================================================

#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class HAHMAStrategy : Strategy
    {
        // ─────────────────────────────────────────────────────────────────────
        //  PARAMÈTRES CONFIGURABLES
        // ─────────────────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(5, 200)]
        [Display(Name = "HMA Lente — période (signal)", Description = "HA Close doit être sous cette HMA pour SHORT / Classic Close au-dessus pour LONG", Order = 1, GroupName = "Paramètres HMA")]
        public int HmaSlowPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(2, 100)]
        [Display(Name = "HMA Rapide — période (sortie)", Description = "Condition de sortie : HA croise cette HMA pour déclencher le TP", Order = 2, GroupName = "Paramètres HMA")]
        public int HmaFastPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.1, 10.0)]
        [Display(Name = "Risque par trade (%)", Description = "Pourcentage du capital risqué par trade — 2% recommandé", Order = 1, GroupName = "Gestion du capital")]
        public double RiskPercent { get; set; }

        [NinjaScriptProperty]
        [Range(1000, 1000000)]
        [Display(Name = "Capital initial ($)", Description = "Capital de départ — base du calcul compounding", Order = 2, GroupName = "Gestion du capital")]
        public double InitialCapitalAmount { get; set; }

        [NinjaScriptProperty]
        [Range(0.01, 500)]
        [Display(Name = "Valeur du point ($)", Description = "ETH CME (full) = 50 | MET (micro) = 0.10 | NQ = 20 | ES = 50 | MNQ = 2", Order = 3, GroupName = "Gestion du capital")]
        public double PointValue { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Contrats maximum", Description = "Plafond de sécurité — nombre max de contrats par trade", Order = 4, GroupName = "Gestion du capital")]
        public int MaxContracts { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        //  VARIABLES INTERNES
        // ─────────────────────────────────────────────────────────────────────

        // Séries Heikin Ashi (calcul manuel — synchronisation garantie)
        private Series<double> haOpen;
        private Series<double> haClose;
        private Series<double> haHigh;
        private Series<double> haLow;

        // Indicateurs HMA
        private HullMA hmaSlow;   // HMA lente — filtre d'entrée
        private HullMA hmaFast;   // HMA rapide — condition de sortie

        // Suivi du capital pour le compounding
        private double currentCapital;

        // ─────────────────────────────────────────────────────────────────────
        //  INITIALISATION
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "HA + HMA Strategy v2";
                Description = "SHORT : Classic RED + HA RED sous HMA50 → SL HA High → TP sur close classic quand HA vert > HMA20\n"
                            + "LONG  : Classic GREEN > HMA50 (sans HA) → SL Classic Low → TP sur close classic quand HA rouge < HMA20\n"
                            + "Capital : 2% par trade, compounding automatique";

                Calculate = Calculate.OnBarClose;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 600;

                // Valeurs par défaut
                HmaSlowPeriod        = 50;
                HmaFastPeriod        = 20;
                RiskPercent          = 2.0;
                InitialCapitalAmount = 50000;
                PointValue           = 50;    // ETH CME full contract ($50/pt) par défaut
                MaxContracts         = 10;
            }
            else if (State == State.Configure)
            {
                // Séries HA
                haOpen  = new Series<double>(this);
                haClose = new Series<double>(this);
                haHigh  = new Series<double>(this);
                haLow   = new Series<double>(this);

                // Indicateurs HMA
                hmaSlow = HullMA(HmaSlowPeriod);
                hmaFast = HullMA(HmaFastPeriod);

                currentCapital = InitialCapitalAmount;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LOGIQUE PRINCIPALE
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            if (CurrentBar < HmaSlowPeriod + 5) return;

            // ══════════════════════════════════════════════════════════════════
            //  CALCUL HEIKIN ASHI
            // ══════════════════════════════════════════════════════════════════

            double haCloseVal = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;
            double haOpenVal  = (CurrentBar == 0)
                ? (Open[0] + Close[0]) / 2.0
                : (haOpen[1] + haClose[1]) / 2.0;

            double haHighVal = Math.Max(High[0], Math.Max(haOpenVal, haCloseVal));
            double haLowVal  = Math.Min(Low[0],  Math.Min(haOpenVal, haCloseVal));

            haOpen[0]  = haOpenVal;
            haClose[0] = haCloseVal;
            haHigh[0]  = haHighVal;
            haLow[0]   = haLowVal;

            // ── Couleurs ─────────────────────────────────────────────────────
            bool haIsRed        = haCloseVal < haOpenVal;
            bool haIsGreen      = haCloseVal > haOpenVal;
            bool classicIsRed   = Close[0] < Open[0];
            bool classicIsGreen = Close[0] > Open[0];

            // ── Valeurs HMA ───────────────────────────────────────────────────
            double hmaSlowVal = hmaSlow[0];
            double hmaFastVal = hmaFast[0];

            // ══════════════════════════════════════════════════════════════════
            //  COMPOUNDING : capital courant = initial + profits réalisés
            // ══════════════════════════════════════════════════════════════════
            currentCapital = InitialCapitalAmount
                           + SystemPerformance.AllTrades.TradesPerformance.Currency.CumProfit;

            // ══════════════════════════════════════════════════════════════════
            //  SORTIES (prioritaires — vérifiées avant les entrées)
            // ══════════════════════════════════════════════════════════════════

            // ── SORTIE SHORT ──────────────────────────────────────────────────
            //  Signal HA : HA VERT + HA Close > HMA(20)
            //  Exécution : close de la bougie CLASSIQUE courante
            if (Position.MarketPosition == MarketPosition.Short)
            {
                if (haIsGreen && haCloseVal > hmaFastVal)
                {
                    ExitShort("TP_Short", "Short_HA");
                    Print($"[HA] ✅ EXIT SHORT (TP) | Bar {CurrentBar} | "
                        + $"Close classic: {Close[0]:F2} | "
                        + $"HA vert > HMA{HmaFastPeriod} ({hmaFastVal:F2}) | "
                        + $"Capital: ${currentCapital:F0}");
                }
                // SL géré automatiquement via SetStopLoss posé à l'entrée
            }

            // ── SORTIE LONG ───────────────────────────────────────────────────
            //  Signal HA : HA ROUGE + HA Close < HMA(20)
            //  Exécution : close de la bougie CLASSIQUE courante
            if (Position.MarketPosition == MarketPosition.Long)
            {
                if (haIsRed && haCloseVal < hmaFastVal)
                {
                    ExitLong("TP_Long", "Long_HA");
                    Print($"[HA] ✅ EXIT LONG (TP) | Bar {CurrentBar} | "
                        + $"Close classic: {Close[0]:F2} | "
                        + $"HA rouge < HMA{HmaFastPeriod} ({hmaFastVal:F2}) | "
                        + $"Capital: ${currentCapital:F0}");
                }
            }

            // ══════════════════════════════════════════════════════════════════
            //  ENTRÉES (seulement si aucune position ouverte)
            // ══════════════════════════════════════════════════════════════════

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                // ── ENTRÉE SHORT ──────────────────────────────────────────────
                //  Candle CLASSIQUE ROUGE + HA ROUGE + HA Close < HMA(50)
                //  SL = High de la bougie HA ROUGE signal
                if (classicIsRed && haIsRed && haCloseVal < hmaSlowVal)
                {
                    double slPrice    = haHighVal;           // SL = HA High
                    double slDistance = slPrice - Close[0];  // Distance en points

                    if (slDistance >= TickSize)
                    {
                        double riskAmount = currentCapital * (RiskPercent / 100.0);
                        int contracts = (int)(riskAmount / (slDistance * PointValue));
                        contracts = Math.Max(1, Math.Min(contracts, MaxContracts));

                        EnterShort(contracts, "Short_HA");
                        SetStopLoss("Short_HA", CalculationMode.Price, slPrice, false);

                        Print($"[HA] 🔻 ENTRÉE SHORT | Bar {CurrentBar} | "
                            + $"Entrée: {Close[0]:F2} | SL (HA High): {slPrice:F2} | "
                            + $"Dist: {slDistance:F2} pts | Risque: ${riskAmount:F0} | "
                            + $"Contrats: {contracts} | Capital: ${currentCapital:F0}");
                    }
                    else
                    {
                        Print($"[HA] ⚠️ SHORT ignoré — SL trop proche ({slDistance:F4} pts) | Bar {CurrentBar}");
                    }
                }

                // ── ENTRÉE LONG ───────────────────────────────────────────────
                //  Candle CLASSIQUE VERTE uniquement + Classic Close > HMA(50)
                //  (PAS de filtre HA pour l'entrée)
                //  SL = Low de la bougie CLASSIQUE signal
                else if (classicIsGreen && Close[0] > hmaSlowVal)
                {
                    double slPrice    = Low[0];              // SL = Classic Low
                    double slDistance = Close[0] - slPrice;  // Distance en points

                    if (slDistance >= TickSize)
                    {
                        double riskAmount = currentCapital * (RiskPercent / 100.0);
                        int contracts = (int)(riskAmount / (slDistance * PointValue));
                        contracts = Math.Max(1, Math.Min(contracts, MaxContracts));

                        EnterLong(contracts, "Long_CL");
                        SetStopLoss("Long_CL", CalculationMode.Price, slPrice, false);

                        Print($"[HA] 🚀 ENTRÉE LONG | Bar {CurrentBar} | "
                            + $"Entrée: {Close[0]:F2} | SL (Classic Low): {slPrice:F2} | "
                            + $"Dist: {slDistance:F2} pts | Risque: ${riskAmount:F0} | "
                            + $"Contrats: {contracts} | Capital: ${currentCapital:F0}");
                    }
                    else
                    {
                        Print($"[HA] ⚠️ LONG ignoré — SL trop proche ({slDistance:F4} pts) | Bar {CurrentBar}");
                    }
                }
            }
        }
    }
}
