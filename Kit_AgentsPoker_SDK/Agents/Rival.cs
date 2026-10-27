using AgentsPoker.Core;

/// <summary>Segundo agente de ejemplo ("compañero"): tight-aggressive simple con el evaluador del SDK. Sin OnHandEnd.</summary>
public sealed class Rival : IPokerAgent
{
    public void Initialize(string agentName) { }

    public ActionType GetAction(PlayerObservation o)
    {
        if (o.Board.Count == 0)
        {
            int a = o.HoleCards[0].Rank, b = o.HoleCards[1].Rank;
            bool pareja = a == b;
            bool altas = a >= 11 && b >= 11;
            bool as_ = a == 14 || b == 14;
            if ((pareja && a >= 9) || altas) return o.CanRaise ? ActionType.RAISE : ActionType.CALL;
            if (pareja || as_ || (a >= 10 && b >= 10)) return o.ToCall <= o.BigBlind ? ActionType.CALL : (o.IsFacingBet ? ActionType.FOLD : ActionType.CALL);
            return o.IsFacingBet ? ActionType.FOLD : ActionType.CALL;
        }
        var cat = HandEvaluator.GetCategory(HandEvaluator.Evaluate(o.HoleCards[0], o.HoleCards[1], o.Board));
        if (cat >= HandCategory.TwoPair) return o.CanRaise ? ActionType.RAISE : ActionType.CALL;
        if (cat == HandCategory.OnePair) return ActionType.CALL;
        return o.IsFacingBet ? ActionType.FOLD : ActionType.CALL;
    }
}
