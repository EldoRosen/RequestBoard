using Sandbox.Game.GameSystems.BankingAndCurrency;

namespace RequestBoard
{
    /// <summary>
    /// Thin wrapper around the game's credit system so any API change
    /// only needs fixing in this one file. Must be called on the game thread.
    /// </summary>
    public static class Bank
    {
        public static long Balance(long identityId) => MyBankingSystem.GetBalance(identityId);

        /// <summary>Positive = give credits, negative = take. Returns false if it can't be applied.</summary>
        public static bool Add(long identityId, long amount) => MyBankingSystem.ChangeBalance(identityId, amount);
    }
}
