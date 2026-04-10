// =============================================================================
//  NQReversalStrategy.cs — Stratégie NinjaTrader 8 pour NQ Futures
//  Auteur     : JP (configurée par OpenClaw)
//  Timeframe  : 10 minutes
//  Instrument : NQ (E-mini Nasdaq-100 Futures)
//  Version    : 1.1 — Avril 2026
//
//  LOGIQUE :
//  ─────────
//  LONG  → Candle ROUGE (corps 10-30 pts) + ADX(14)≤20 + HA ROUGE
//           Attendre breakout du High rouge + 2 pts (max 3 barres)
//           SL = entrée - 15 pts | TP = entrée + 40 pts
//
//  SHORT → Candle VERTE (corps 10-30 pts) + ADX(14)≤20 + HA VERTE
//           Attendre breakout du Low vert - 2 pts (max 3 barres)
//           SL = entrée + 15 pts | TP = entrée - 40 pts
//
//  FILTRE HA :
//  ───────────
//  La bougie Heikin Ashi (calculée manuellement) doit être de la MÊME couleur
//  que la bougie classique pour valider le signal.
//  → Évite les faux signaux en début de retournement indécis.
//
//  Conversion NQ : 1 point = 4 ticks (tick size = 0.25 pt)
//  SL 15 pts = 60 ticks | TP 40 pts = 160 ticks
//
//  Heures actives : 09h30 – 15h50 ET (heure de New York)
//  Sortie forcée  : 15h50 ET / 21h50 UTC
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
    public class NQReversalStrategy : Strategy
    {
        // ─────────────────────────────────────────────────────────────────────
        //  PARAMÈTRES CONFIGURABLES (visibles dans l'interface NinjaTrader)
        // ─────────────────────────────────────────────────────────────────────

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "ADX Seuil (≤)", Description = "ADX(14) doit être ≤ à cette valeur pour valider le signal", Order = 1, GroupName = "Filtres")]
        public int AdxThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Corps Min (points)", Description = "Taille minimale du corps de la bougie signal (en points NQ)", Order = 2, GroupName = "Filtres")]
        public int BodyMinPts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Corps Max (points)", Description = "Taille maximale du corps de la bougie signal (en points NQ)", Order = 3, GroupName = "Filtres")]
        public int BodyMaxPts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Breakout (points)", Description = "Points au-dessus du High (ou sous le Low) pour déclencher l'entrée", Order = 4, GroupName = "Filtres")]
        public int BreakoutPts { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Barres d'attente max", Description = "Nombre maximum de barres pour attendre le breakout avant expiration du signal", Order = 5, GroupName = "Filtres")]
        public int MaxWaitBars { get; set; }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Stop Loss (points)", Description = "Distance du Stop Loss en points NQ (1 pt = 4 ticks)", Order = 1, GroupName = "Gestion du risque")]
        public int SlPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 200)]
        [Display(Name = "Take Profit (points)", Description = "Distance du Take Profit en points NQ (1 pt = 4 ticks)", Order = 2, GroupName = "Gestion du risque")]
        public int TpPoints { get; set; }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Nombre de contrats", Description = "Nombre de contrats NQ à trader (1 ou 2 recommandé)", Order = 3, GroupName = "Gestion du risque")]
        public int Contracts { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        //  VARIABLES INTERNES
        // ─────────────────────────────────────────────────────────────────────

        private ADX adxIndicator;       // Indicateur ADX(14)

        // Séries Heikin Ashi calculées manuellement
        // (plus fiable qu'un indicateur secondaire dans NT8 pour les stratégies)
        private Series<double> haOpen;
        private Series<double> haClose;

        private int    signalBar       = -1;
        private double signalLevel     = 0;
        private int    signalDirection = 0;

        // ─────────────────────────────────────────────────────────────────────
        //  INITIALISATION
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name        = "NQ Reversal ADX+HA Strategy";
                Description = "Stratégie de retournement NQ 10M\n"
                            + "Candle rouge/verte + ADX≤20 + Heikin Ashi même couleur + Breakout\n"
                            + "LONG sur breakout du High rouge | SHORT sur breakout du Low vert\n"
                            + "Heures : 09h30–15h50 ET uniquement";

                Calculate = Calculate.OnBarClose;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 600;

                // Valeurs par défaut
                AdxThreshold = 20;
                BodyMinPts   = 10;
                BodyMaxPts   = 30;
                BreakoutPts  = 2;
                MaxWaitBars  = 3;
                SlPoints     = 15;
                TpPoints     = 40;
                Contracts    = 1;
            }
            else if (State == State.Configure)
            {
                adxIndicator = ADX(14);

                // Initialisation des séries HA
                haOpen  = new Series<double>(this);
                haClose = new Series<double>(this);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LOGIQUE PRINCIPALE
        // ─────────────────────────────────────────────────────────────────────

        protected override void OnBarUpdate()
        {
            if (CurrentBar < 20) return;

            // ── CALCUL HEIKIN ASHI ───────────────────────────────────────────
            // HA Close = moyenne des 4 prix OHLC de la bougie classique
            double haCloseVal = (Open[0] + High[0] + Low[0] + Close[0]) / 4.0;

            // HA Open = moyenne (HA Open précédent + HA Close précédent)
            // À la première barre : initialisation avec (Open + Close) / 2
            double haOpenVal;
            if (CurrentBar == 0)
                haOpenVal = (Open[0] + Close[0]) / 2.0;
            else
                haOpenVal = (haOpen[1] + haClose[1]) / 2.0;

            // Stocker dans les séries pour la barre suivante
            haOpen[0]  = haOpenVal;
            haClose[0] = haCloseVal;

            // Couleur de la bougie HA
            bool haIsRed   = haCloseVal < haOpenVal;  // HA baissière (rouge)
            bool haIsGreen = haCloseVal > haOpenVal;  // HA haussière (verte)

            // ── FILTRE HORAIRE ───────────────────────────────────────────────
            int barTime = ToTime(Time[0]);
            if (barTime < 093000 || barTime >= 155000) return;

            // ── VARIABLES DE LA BOUGIE CLASSIQUE ────────────────────────────
            double body     = Math.Abs(Close[0] - Open[0]);
            double adxValue = adxIndicator[0];
            bool   isRed    = Close[0] < Open[0];
            bool   isGreen  = Close[0] > Open[0];
            bool   bodyOk   = body >= BodyMinPts && body <= BodyMaxPts;
            bool   adxOk    = adxValue <= AdxThreshold;

            // ── DÉTECTION SIGNAL ─────────────────────────────────────────────
            if (signalBar == -1 && Position.MarketPosition == MarketPosition.Flat)
            {
                if (isRed && bodyOk && adxOk && haIsRed)
                {
                    // ✅ SIGNAL LONG
                    // Candle classique ROUGE + ADX faible + HA aussi ROUGE
                    signalBar       = CurrentBar;
                    signalLevel     = High[0] + BreakoutPts;
                    signalDirection = 1;

                    Print($"[NQ] Signal LONG détecté | Barre {CurrentBar} | Entrée cible: {signalLevel:F2} | ADX: {adxValue:F1} | HA: rouge ✓");
                }
                else if (isGreen && bodyOk && adxOk && haIsGreen)
                {
                    // ✅ SIGNAL SHORT
                    // Candle classique VERTE + ADX faible + HA aussi VERTE
                    signalBar       = CurrentBar;
                    signalLevel     = Low[0] - BreakoutPts;
                    signalDirection = -1;

                    Print($"[NQ] Signal SHORT détecté | Barre {CurrentBar} | Entrée cible: {signalLevel:F2} | ADX: {adxValue:F1} | HA: verte ✓");
                }
                else if ((isRed && bodyOk && adxOk && !haIsRed) || (isGreen && bodyOk && adxOk && !haIsGreen))
                {
                    // ℹ️ Signal ignoré — candle et HA de couleurs différentes
                    Print($"[NQ] Signal ignoré — HA et candle classique de couleurs différentes | Barre {CurrentBar} | ADX: {adxValue:F1}");
                }
            }

            // ── GESTION DU SIGNAL EN ATTENTE ────────────────────────────────
            if (signalBar != -1 && Position.MarketPosition == MarketPosition.Flat)
            {
                int barsElapsed = CurrentBar - signalBar;

                if (barsElapsed > MaxWaitBars)
                {
                    Print($"[NQ] Signal expiré après {barsElapsed} barres | Niveau: {signalLevel:F2}");
                    ResetSignal();
                }
                else if (signalDirection == 1 && High[0] >= signalLevel)
                {
                    // 🚀 ENTRÉE LONG
                    Print($"[NQ] ENTRÉE LONG | Prix: {signalLevel:F2} | SL: -{SlPoints} pts | TP: +{TpPoints} pts | Contrats: {Contracts}");

                    EnterLong(Contracts, "Long_NQ");
                    SetStopLoss("Long_NQ",     CalculationMode.Ticks, SlPoints * 4, false);
                    SetProfitTarget("Long_NQ", CalculationMode.Ticks, TpPoints * 4);

                    ResetSignal();
                }
                else if (signalDirection == -1 && Low[0] <= signalLevel)
                {
                    // 🔻 ENTRÉE SHORT
                    Print($"[NQ] ENTRÉE SHORT | Prix: {signalLevel:F2} | SL: +{SlPoints} pts | TP: -{TpPoints} pts | Contrats: {Contracts}");

                    EnterShort(Contracts, "Short_NQ");
                    SetStopLoss("Short_NQ",     CalculationMode.Ticks, SlPoints * 4, false);
                    SetProfitTarget("Short_NQ", CalculationMode.Ticks, TpPoints * 4);

                    ResetSignal();
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  UTILITAIRE — Réinitialiser le signal
        // ─────────────────────────────────────────────────────────────────────

        private void ResetSignal()
        {
            signalBar       = -1;
            signalLevel     = 0;
            signalDirection = 0;
        }
    }
}
