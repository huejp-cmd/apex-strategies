# Trading Strategies — JP

## HA + HMA Strategy (Pine Script v5 — TradingView)
Fichier : `HA_HMA_Strategy_TV.pine`

**Logique :**
- SHORT : Candle classique rouge + HA rouge + HA Close < HMA(50) | SL = High bougie HA | TP = HA vert + HA Close > HMA(20)
- LONG  : Candle classique verte + Classic Close > HMA(50) | SL = Low bougie classique | TP = HA rouge + HA Close < HMA(20)
- Capital : $50 000 | Risque : 2% par trade (compounding)
- Instrument recommandé : ETHUSDT.P

## HA + HMA Strategy (NinjaTrader 8 C#)
Fichier : `HAHMAStrategy.cs`

Même logique que le Pine Script, adaptée pour NinjaTrader 8 (Apex Trader Funding).

## NQ Reversal Strategy (NinjaTrader 8 C#)
Fichier : `NQReversalStrategy.cs`

Stratégie de retournement sur NQ 10M — Candle rouge/verte + ADX≤20 + Heikin Ashi même couleur + Breakout.
