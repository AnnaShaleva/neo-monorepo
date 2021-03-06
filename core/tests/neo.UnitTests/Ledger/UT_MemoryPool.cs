using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Neo.Cryptography;
using Neo.IO;
using Neo.Ledger;
using Neo.Network.P2P.Payloads;
using Neo.Persistence;
using Neo.Plugins;
using Neo.SmartContract;
using Neo.SmartContract.Native;
using Neo.SmartContract.Native.Tokens;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Neo.UnitTests.Ledger
{
    internal class TestIMemoryPoolTxObserverPlugin : Plugin, IMemoryPoolTxObserverPlugin
    {
        protected override void Configure() { }
        public void TransactionAdded(Transaction tx) { }
        public void TransactionsRemoved(MemoryPoolTxRemovalReason reason, IEnumerable<Transaction> transactions) { }
    }

    [TestClass]
    public class UT_MemoryPool
    {
        private static NeoSystem testBlockchain;

        private const byte Prefix_MaxTransactionsPerBlock = 23;
        private const byte Prefix_FeePerByte = 10;
        private readonly UInt160 senderAccount = UInt160.Zero;
        private MemoryPool _unit;
        private MemoryPool _unit2;
        private TestIMemoryPoolTxObserverPlugin plugin;

        [ClassInitialize]
        public static void TestSetup(TestContext ctx)
        {
            testBlockchain = TestBlockchain.TheNeoSystem;
        }

        [TestInitialize]
        public void TestSetup()
        {
            // protect against external changes on TimeProvider
            TimeProvider.ResetToDefault();

            TestBlockchain.InitializeMockNeoSystem();

            // Create a MemoryPool with capacity of 100
            _unit = new MemoryPool(TestBlockchain.TheNeoSystem, 100);
            _unit.LoadPolicy(Blockchain.Singleton.GetSnapshot());

            // Verify capacity equals the amount specified
            _unit.Capacity.Should().Be(100);

            _unit.VerifiedCount.Should().Be(0);
            _unit.UnVerifiedCount.Should().Be(0);
            _unit.Count.Should().Be(0);
            _unit2 = new MemoryPool(TestBlockchain.TheNeoSystem, 0);
            plugin = new TestIMemoryPoolTxObserverPlugin();
        }

        [TestCleanup]
        public void CleanUp()
        {
            Plugin.TxObserverPlugins.Remove(plugin);
        }

        long LongRandom(long min, long max, Random rand)
        {
            // Only returns positive random long values.
            long longRand = (long)rand.NextBigInteger(63);
            return longRand % (max - min) + min;
        }

        private Transaction CreateTransactionWithFee(long fee)
        {
            Random random = new Random();
            var randomBytes = new byte[16];
            random.NextBytes(randomBytes);
            Mock<Transaction> mock = new Mock<Transaction>();
            mock.Setup(p => p.Verify(It.IsAny<StoreView>(), It.IsAny<TransactionVerificationContext>())).Returns(VerifyResult.Succeed);
            mock.Setup(p => p.VerifyStateDependent(It.IsAny<StoreView>(), It.IsAny<TransactionVerificationContext>())).Returns(VerifyResult.Succeed);
            mock.Setup(p => p.VerifyStateIndependent()).Returns(VerifyResult.Succeed);
            mock.Object.Script = randomBytes;
            mock.Object.NetworkFee = fee;
            mock.Object.Attributes = Array.Empty<TransactionAttribute>();
            mock.Object.Signers = new Signer[] { new Signer() { Account = senderAccount, Scopes = WitnessScope.None } };
            mock.Object.Witnesses = new[]
            {
                new Witness
                {
                    InvocationScript = new byte[0],
                    VerificationScript = new byte[0]
                }
            };
            return mock.Object;
        }

        private Transaction CreateTransactionWithFeeAndBalanceVerify(long fee)
        {
            Random random = new Random();
            var randomBytes = new byte[16];
            random.NextBytes(randomBytes);
            Mock<Transaction> mock = new Mock<Transaction>();
            UInt160 sender = senderAccount;
            mock.Setup(p => p.Verify(It.IsAny<StoreView>(), It.IsAny<TransactionVerificationContext>())).Returns(VerifyResult.Succeed);
            mock.Setup(p => p.VerifyStateDependent(It.IsAny<StoreView>(), It.IsAny<TransactionVerificationContext>())).Returns((StoreView snapshot, TransactionVerificationContext context) => context.CheckTransaction(mock.Object, snapshot) ? VerifyResult.Succeed : VerifyResult.InsufficientFunds);
            mock.Setup(p => p.VerifyStateIndependent()).Returns(VerifyResult.Succeed);
            mock.Object.Script = randomBytes;
            mock.Object.NetworkFee = fee;
            mock.Object.Attributes = Array.Empty<TransactionAttribute>();
            mock.Object.Signers = new Signer[] { new Signer() { Account = senderAccount, Scopes = WitnessScope.None } };
            mock.Object.Witnesses = new[]
            {
                new Witness
                {
                    InvocationScript = new byte[0],
                    VerificationScript = new byte[0]
                }
            };
            return mock.Object;
        }

        private Transaction CreateTransaction(long fee = -1)
        {
            if (fee != -1)
                return CreateTransactionWithFee(fee);
            return CreateTransactionWithFee(LongRandom(100000, 100000000, TestUtils.TestRandom));
        }

        private void AddTransactions(int count)
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            for (int i = 0; i < count; i++)
            {
                var txToAdd = CreateTransaction();
                _unit.TryAdd(txToAdd, snapshot);
            }

            Console.WriteLine($"created {count} tx");
        }

        private void AddTransaction(Transaction txToAdd)
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            _unit.TryAdd(txToAdd, snapshot);
        }

        private void AddTransactionsWithBalanceVerify(int count, long fee, SnapshotView snapshot)
        {
            for (int i = 0; i < count; i++)
            {
                var txToAdd = CreateTransactionWithFeeAndBalanceVerify(fee);
                _unit.TryAdd(txToAdd, snapshot);
            }

            Console.WriteLine($"created {count} tx");
        }

        [TestMethod]
        public void CapacityTest()
        {
            // Add over the capacity items, verify that the verified count increases each time
            AddTransactions(101);

            Console.WriteLine($"VerifiedCount: {_unit.VerifiedCount} Count {_unit.SortedTxCount}");

            _unit.SortedTxCount.Should().Be(100);
            _unit.VerifiedCount.Should().Be(100);
            _unit.UnVerifiedCount.Should().Be(0);
            _unit.Count.Should().Be(100);
        }

        [TestMethod]
        public void BlockPersistMovesTxToUnverifiedAndReverification()
        {
            AddTransactions(70);

            _unit.SortedTxCount.Should().Be(70);

            var block = new Block
            {
                Transactions = _unit.GetSortedVerifiedTransactions().Take(10)
                    .Concat(_unit.GetSortedVerifiedTransactions().Take(5)).ToArray()
            };
            _unit.UpdatePoolForBlockPersisted(block, Blockchain.Singleton.GetSnapshot());
            _unit.InvalidateVerifiedTransactions();
            _unit.SortedTxCount.Should().Be(0);
            _unit.UnverifiedSortedTxCount.Should().Be(60);

            _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(10, Blockchain.Singleton.GetSnapshot());
            _unit.SortedTxCount.Should().Be(10);
            _unit.UnverifiedSortedTxCount.Should().Be(50);

            _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(10, Blockchain.Singleton.GetSnapshot());
            _unit.SortedTxCount.Should().Be(20);
            _unit.UnverifiedSortedTxCount.Should().Be(40);

            _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(10, Blockchain.Singleton.GetSnapshot());
            _unit.SortedTxCount.Should().Be(30);
            _unit.UnverifiedSortedTxCount.Should().Be(30);

            _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(10, Blockchain.Singleton.GetSnapshot());
            _unit.SortedTxCount.Should().Be(40);
            _unit.UnverifiedSortedTxCount.Should().Be(20);

            _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(10, Blockchain.Singleton.GetSnapshot());
            _unit.SortedTxCount.Should().Be(50);
            _unit.UnverifiedSortedTxCount.Should().Be(10);

            _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(10, Blockchain.Singleton.GetSnapshot());
            _unit.SortedTxCount.Should().Be(60);
            _unit.UnverifiedSortedTxCount.Should().Be(0);
        }

        [TestMethod]
        public void BlockPersistAndReverificationWillAbandonTxAsBalanceTransfered()
        {
            SnapshotView snapshot = Blockchain.Singleton.GetSnapshot();
            BigInteger balance = NativeContract.GAS.BalanceOf(snapshot, senderAccount);
            ApplicationEngine engine = ApplicationEngine.Create(TriggerType.Application, null, snapshot, long.MaxValue);
            NativeContract.GAS.Burn(engine, UInt160.Zero, balance);
            NativeContract.GAS.Mint(engine, UInt160.Zero, 70);

            long txFee = 1;
            AddTransactionsWithBalanceVerify(70, txFee, snapshot);

            _unit.SortedTxCount.Should().Be(70);

            var block = new Block
            {
                Transactions = _unit.GetSortedVerifiedTransactions().Take(10).ToArray()
            };

            // Simulate the transfer process in tx by burning the balance
            UInt160 sender = block.Transactions[0].Sender;

            ApplicationEngine applicationEngine = ApplicationEngine.Create(TriggerType.All, block, snapshot, (long)balance);
            NativeContract.GAS.Burn(applicationEngine, sender, NativeContract.GAS.BalanceOf(snapshot, sender));
            NativeContract.GAS.Mint(applicationEngine, sender, txFee * 30); // Set the balance to meet 30 txs only

            // Persist block and reverify all the txs in mempool, but half of the txs will be discarded
            _unit.UpdatePoolForBlockPersisted(block, snapshot);
            _unit.SortedTxCount.Should().Be(30);
            _unit.UnverifiedSortedTxCount.Should().Be(0);

            // Revert the balance
            NativeContract.GAS.Burn(applicationEngine, sender, txFee * 30);
            NativeContract.GAS.Mint(applicationEngine, sender, balance);
        }

        private void VerifyTransactionsSortedDescending(IEnumerable<Transaction> transactions)
        {
            Transaction lastTransaction = null;
            foreach (var tx in transactions)
            {
                if (lastTransaction != null)
                {
                    if (lastTransaction.FeePerByte == tx.FeePerByte)
                    {
                        if (lastTransaction.NetworkFee == tx.NetworkFee)
                            lastTransaction.Hash.Should().BeLessThan(tx.Hash);
                        else
                            lastTransaction.NetworkFee.Should().BeGreaterThan(tx.NetworkFee);
                    }
                    else
                    {
                        lastTransaction.FeePerByte.Should().BeGreaterThan(tx.FeePerByte);
                    }
                }
                lastTransaction = tx;
            }
        }

        [TestMethod]
        public void VerifySortOrderAndThatHighetFeeTransactionsAreReverifiedFirst()
        {
            AddTransactions(100);

            var sortedVerifiedTxs = _unit.GetSortedVerifiedTransactions().ToList();
            // verify all 100 transactions are returned in sorted order
            sortedVerifiedTxs.Count.Should().Be(100);
            VerifyTransactionsSortedDescending(sortedVerifiedTxs);

            // move all to unverified
            var block = new Block { Transactions = new Transaction[0] };
            _unit.UpdatePoolForBlockPersisted(block, Blockchain.Singleton.GetSnapshot());
            _unit.InvalidateVerifiedTransactions();
            _unit.SortedTxCount.Should().Be(0);
            _unit.UnverifiedSortedTxCount.Should().Be(100);

            // We can verify the order they are re-verified by reverifying 2 at a time
            while (_unit.UnVerifiedCount > 0)
            {
                _unit.GetVerifiedAndUnverifiedTransactions(out var sortedVerifiedTransactions, out var sortedUnverifiedTransactions);
                sortedVerifiedTransactions.Count().Should().Be(0);
                var sortedUnverifiedArray = sortedUnverifiedTransactions.ToArray();
                VerifyTransactionsSortedDescending(sortedUnverifiedArray);
                var maxTransaction = sortedUnverifiedArray.First();
                var minTransaction = sortedUnverifiedArray.Last();

                // reverify 1 high priority and 1 low priority transaction
                _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(1, Blockchain.Singleton.GetSnapshot());
                var verifiedTxs = _unit.GetSortedVerifiedTransactions().ToArray();
                verifiedTxs.Length.Should().Be(1);
                verifiedTxs[0].Should().BeEquivalentTo(maxTransaction);
                var blockWith2Tx = new Block { Transactions = new[] { maxTransaction, minTransaction } };
                // verify and remove the 2 transactions from the verified pool
                _unit.UpdatePoolForBlockPersisted(blockWith2Tx, Blockchain.Singleton.GetSnapshot());
                _unit.InvalidateVerifiedTransactions();
                _unit.SortedTxCount.Should().Be(0);
            }
            _unit.UnverifiedSortedTxCount.Should().Be(0);
        }

        void VerifyCapacityThresholdForAttemptingToAddATransaction()
        {
            var sortedVerified = _unit.GetSortedVerifiedTransactions().ToArray();

            var txBarelyWontFit = CreateTransactionWithFee(sortedVerified.Last().NetworkFee - 1);
            _unit.CanTransactionFitInPool(txBarelyWontFit).Should().Be(false);
            var txBarelyFits = CreateTransactionWithFee(sortedVerified.Last().NetworkFee + 1);
            _unit.CanTransactionFitInPool(txBarelyFits).Should().Be(true);
        }

        [TestMethod]
        public void VerifyCanTransactionFitInPoolWorksAsIntended()
        {
            AddTransactions(100);
            VerifyCapacityThresholdForAttemptingToAddATransaction();
            AddTransactions(50);
            VerifyCapacityThresholdForAttemptingToAddATransaction();
            AddTransactions(50);
            VerifyCapacityThresholdForAttemptingToAddATransaction();
        }

        [TestMethod]
        public void CapacityTestWithUnverifiedHighProirtyTransactions()
        {
            // Verify that unverified high priority transactions will not be pushed out of the queue by incoming
            // low priority transactions

            // Fill pool with high priority transactions
            AddTransactions(99);

            // move all to unverified
            var block = new Block { Transactions = new Transaction[0] };
            _unit.UpdatePoolForBlockPersisted(block, Blockchain.Singleton.GetSnapshot());

            _unit.CanTransactionFitInPool(CreateTransaction()).Should().Be(true);
            AddTransactions(1);
            _unit.CanTransactionFitInPool(CreateTransactionWithFee(0)).Should().Be(false);
        }

        [TestMethod]
        public void TestInvalidateAll()
        {
            AddTransactions(30);

            _unit.UnverifiedSortedTxCount.Should().Be(0);
            _unit.SortedTxCount.Should().Be(30);
            _unit.InvalidateAllTransactions();
            _unit.UnverifiedSortedTxCount.Should().Be(30);
            _unit.SortedTxCount.Should().Be(0);
        }

        [TestMethod]
        public void TestContainsKey()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            AddTransactions(10);

            var txToAdd = CreateTransaction();
            _unit.TryAdd(txToAdd, snapshot);
            _unit.ContainsKey(txToAdd.Hash).Should().BeTrue();
            _unit.InvalidateVerifiedTransactions();
            _unit.ContainsKey(txToAdd.Hash).Should().BeTrue();
        }

        [TestMethod]
        public void TestGetEnumerator()
        {
            AddTransactions(10);
            _unit.InvalidateVerifiedTransactions();
            IEnumerator<Transaction> enumerator = _unit.GetEnumerator();
            foreach (Transaction tx in _unit)
            {
                enumerator.MoveNext();
                enumerator.Current.Should().BeSameAs(tx);
            }
        }

        [TestMethod]
        public void TestIEnumerableGetEnumerator()
        {
            AddTransactions(10);
            _unit.InvalidateVerifiedTransactions();
            IEnumerable enumerable = _unit;
            var enumerator = enumerable.GetEnumerator();
            foreach (Transaction tx in _unit)
            {
                enumerator.MoveNext();
                enumerator.Current.Should().BeSameAs(tx);
            }
        }

        [TestMethod]
        public void TestGetVerifiedTransactions()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var tx1 = CreateTransaction();
            var tx2 = CreateTransaction();
            _unit.TryAdd(tx1, snapshot);
            _unit.InvalidateVerifiedTransactions();
            _unit.TryAdd(tx2, snapshot);
            IEnumerable<Transaction> enumerable = _unit.GetVerifiedTransactions();
            enumerable.Count().Should().Be(1);
            var enumerator = enumerable.GetEnumerator();
            enumerator.MoveNext();
            enumerator.Current.Should().BeSameAs(tx2);
        }

        [TestMethod]
        public void TestReVerifyTopUnverifiedTransactionsIfNeeded()
        {
            _unit = new MemoryPool(TestBlockchain.TheNeoSystem, 600);
            _unit.LoadPolicy(Blockchain.Singleton.GetSnapshot());
            AddTransaction(CreateTransaction(100000001));
            AddTransaction(CreateTransaction(100000001));
            AddTransaction(CreateTransaction(100000001));
            AddTransaction(CreateTransaction(1));
            _unit.VerifiedCount.Should().Be(4);
            _unit.UnVerifiedCount.Should().Be(0);

            _unit.InvalidateVerifiedTransactions();
            _unit.VerifiedCount.Should().Be(0);
            _unit.UnVerifiedCount.Should().Be(4);

            AddTransactions(511); // Max per block currently is 512
            _unit.VerifiedCount.Should().Be(511);
            _unit.UnVerifiedCount.Should().Be(4);

            var result = _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(1, Blockchain.Singleton.GetSnapshot());
            result.Should().BeTrue();
            _unit.VerifiedCount.Should().Be(512);
            _unit.UnVerifiedCount.Should().Be(3);

            result = _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(2, Blockchain.Singleton.GetSnapshot());
            result.Should().BeTrue();
            _unit.VerifiedCount.Should().Be(514);
            _unit.UnVerifiedCount.Should().Be(1);

            result = _unit.ReVerifyTopUnverifiedTransactionsIfNeeded(3, Blockchain.Singleton.GetSnapshot());
            result.Should().BeFalse();
            _unit.VerifiedCount.Should().Be(515);
            _unit.UnVerifiedCount.Should().Be(0);
        }

        [TestMethod]
        public void TestTryAdd()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var tx1 = CreateTransaction();
            _unit.TryAdd(tx1, snapshot).Should().Be(VerifyResult.Succeed);
            _unit.TryAdd(tx1, snapshot).Should().NotBe(VerifyResult.Succeed);
            _unit2.TryAdd(tx1, snapshot).Should().NotBe(VerifyResult.Succeed);
        }

        [TestMethod]
        public void TestTryGetValue()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            var tx1 = CreateTransaction();
            _unit.TryAdd(tx1, snapshot);
            _unit.TryGetValue(tx1.Hash, out Transaction tx).Should().BeTrue();
            tx.Should().BeEquivalentTo(tx1);

            _unit.InvalidateVerifiedTransactions();
            _unit.TryGetValue(tx1.Hash, out tx).Should().BeTrue();
            tx.Should().BeEquivalentTo(tx1);

            var tx2 = CreateTransaction();
            _unit.TryGetValue(tx2.Hash, out tx).Should().BeFalse();
        }

        [TestMethod]
        public void TestUpdatePoolForBlockPersisted()
        {
            var snapshot = Blockchain.Singleton.GetSnapshot();
            byte[] transactionsPerBlock = { 0x18, 0x00, 0x00, 0x00 }; // 24
            byte[] feePerByte = { 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00 }; // 1048576
            StorageItem item1 = new StorageItem
            {
                Value = transactionsPerBlock
            };
            StorageItem item2 = new StorageItem
            {
                Value = feePerByte
            };
            var key1 = CreateStorageKey(Prefix_MaxTransactionsPerBlock);
            var key2 = CreateStorageKey(Prefix_FeePerByte);
            key1.Id = NativeContract.Policy.Id;
            key2.Id = NativeContract.Policy.Id;
            snapshot.Storages.Add(key1, item1);
            snapshot.Storages.Add(key2, item2);

            var tx1 = CreateTransaction();
            var tx2 = CreateTransaction();
            Transaction[] transactions = { tx1, tx2 };
            _unit.TryAdd(tx1, snapshot);

            var block = new Block { Transactions = transactions };

            _unit.UnVerifiedCount.Should().Be(0);
            _unit.VerifiedCount.Should().Be(1);

            _unit.UpdatePoolForBlockPersisted(block, snapshot);

            _unit.UnVerifiedCount.Should().Be(0);
            _unit.VerifiedCount.Should().Be(0);
        }

        public StorageKey CreateStorageKey(byte prefix, byte[] key = null)
        {
            StorageKey storageKey = new StorageKey
            {
                Id = 0,
                Key = new byte[sizeof(byte) + (key?.Length ?? 0)]
            };
            storageKey.Key[0] = prefix;
            if (key != null)
                Buffer.BlockCopy(key, 0, storageKey.Key, 1, key.Length);
            return storageKey;
        }
    }
}
