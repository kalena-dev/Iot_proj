using MongoDB.Driver;
using SmartStore.Data;
using SmartStore.Models;
using SmartStore.Services;

namespace SmartStore.Tests;

public sealed class MongoTransactionIntegrationTests
{
    [MongoReplicaSetFact]
    public async Task RepeatedReceivingPersistsEachDeliveryAndAccumulatesStock()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        var first = MongoTestDatabase.NewReception(product, 5, day: 1);
        var second = MongoTestDatabase.NewReception(product, 7, day: 2);

        var firstResult = await db.Products.ReceiveStockAsync(first, db.Token);
        var secondResult = await db.Products.ReceiveStockAsync(second, db.Token);

        Assert.Equal(5, firstResult.QuantityInStock);
        Assert.Equal(12, secondResult.QuantityInStock);
        Assert.Equal(12, (await db.ReadProductAsync(product))!.QuantityInStock);
        var history = await db.ReadReceptionsAsync(product);
        Assert.Equal(2, history.Count);
        var savedFirst = Assert.Single(history, r => r.Id == first.Id);
        Assert.Equal(5, savedFirst.Quantity);
        Assert.Equal(first.ReceivedOn, savedFirst.ReceivedOn);
        Assert.Equal(first.ExpiresOn, savedFirst.ExpiresOn);
        var savedSecond = Assert.Single(history, r => r.Id == second.Id);
        Assert.Equal(7, savedSecond.Quantity);
        Assert.Equal(second.ReceivedOn, savedSecond.ReceivedOn);
        Assert.Equal(second.ExpiresOn, savedSecond.ExpiresOn);
    }

    [MongoReplicaSetFact]
    public async Task ConcurrentReceivingPreservesBothDeliveriesWithoutLostStock()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        var first = MongoTestDatabase.NewReception(product, 5, day: 1);
        var second = MongoTestDatabase.NewReception(product, 7, day: 2);

        await RunTogetherAsync(
            () => db.Products.ReceiveStockAsync(first, db.Token),
            () => db.Products.ReceiveStockAsync(second, db.Token));

        Assert.Equal(12, (await db.ReadProductAsync(product))!.QuantityInStock);
        var history = await db.ReadReceptionsAsync(product);
        Assert.Equal(2, history.Count);
        Assert.Contains(history, r => r.Id == first.Id && r.Quantity == 5);
        Assert.Contains(history, r => r.Id == second.Id && r.Quantity == 7);
    }

    [MongoReplicaSetFact]
    public Task ConcurrentDuplicateUpcAllowsOnlyOneProduct() => AssertConcurrentDuplicateAsync("UPC");

    [MongoReplicaSetFact]
    public async Task ConcurrentReceivingOfOneTagRegistersItForOneProductOnly()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(ordinal: 1);
        var second = await db.AddProductAsync(ordinal: 2);

        var failures = await RunTogetherAsync(
            () => Record.ExceptionAsync(() => db.Products.ReceiveStockAsync(
                MongoTestDatabase.NewTaggedReception(first, 1, "E200AA01"), db.Token)),
            () => Record.ExceptionAsync(() => db.Products.ReceiveStockAsync(
                MongoTestDatabase.NewTaggedReception(second, 1, "E200AA01"), db.Token)));

        Assert.Single(failures, failure => failure is null);
        Assert.IsType<InvalidOperationException>(Assert.Single(failures, failure => failure is not null));
        var stock = (await db.ReadProductAsync(first))!.QuantityInStock + (await db.ReadProductAsync(second))!.QuantityInStock;
        Assert.Equal(1, stock);
        Assert.Single((await db.ReadItemsAsync(first)).Concat(await db.ReadItemsAsync(second)));
    }

    [MongoReplicaSetFact]
    public async Task ReceivingTagsRegistersOneItemPerUnitAndSkipsRescans()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        var first = MongoTestDatabase.NewTaggedReception(product, 1, "e200aa01", "E200AA02", "E200AA02");
        first.Quantity = 3;

        var afterFirst = await db.Products.ReceiveStockAsync(first, db.Token);
        var rescan = MongoTestDatabase.NewTaggedReception(product, 2, "E200AA02", "E200AA03");
        var afterRescan = await db.Products.ReceiveStockAsync(rescan, db.Token);

        Assert.Equal(5, afterFirst.QuantityInStock);
        Assert.Equal(["E200AA01", "E200AA02"], first.Epcs);
        Assert.Equal(5, first.Quantity);
        Assert.Equal(["E200AA03"], rescan.Epcs);
        Assert.Equal(1, rescan.Quantity);
        Assert.Equal(6, afterRescan.QuantityInStock);
        var items = await db.ReadItemsAsync(product);
        Assert.Equal(["E200AA01", "E200AA02", "E200AA03"], items.Select(i => i.Epc).Order());
        Assert.All(items, i => Assert.Equal(Item.InStock, i.Status));
        Assert.Equal(first.Id, items.Single(i => i.Epc == "E200AA01").ReceptionId);
        Assert.Equal(3, await db.Products.CountTaggedInStockAsync(product.Id!, db.Token));
        Assert.Equal((product.Id, true), (await db.Products.ResolveCodeAsync("e200aa03", db.Token)) is { } r ? (r.Product.Id, r.IsTag) : default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(product, 3, "E200AA01"), db.Token));
        Assert.Equal(6, (await db.ReadProductAsync(product))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task TagsOfAnotherProductOrSoldUnitsRejectTheWholeDelivery()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var chair = await db.AddProductAsync(ordinal: 1);
        var table = await db.AddProductAsync(ordinal: 2);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(chair, 1, "C0C0", "C0C1"), db.Token);
        await db.MarkSoldAsync("C0C1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(table, 2, "7AB1E0", "C0C0"), db.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(chair, 2, "C0C2", "C0C1"), db.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(chair, 2, "not-a-tag"), db.Token));

        Assert.Equal(0, (await db.ReadProductAsync(table))!.QuantityInStock);
        Assert.Empty(await db.ReadItemsAsync(table));
        Assert.Equal(2, (await db.ReadItemsAsync(chair)).Count);
        Assert.Single(await db.ReadReceptionsAsync(chair));
    }

    [MongoReplicaSetFact]
    public async Task UndoUnregistersTagsUnlessOneWasSold()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        var kept = MongoTestDatabase.NewTaggedReception(product, 1, "AB01", "AB02");
        var undone = MongoTestDatabase.NewTaggedReception(product, 2, "AB03");
        undone.Quantity = 2;
        await db.Products.ReceiveStockAsync(kept, db.Token);
        await db.Products.ReceiveStockAsync(undone, db.Token);

        Assert.True(await db.Products.DeleteReceptionAsync(undone.Id!, db.Token));
        Assert.Equal(2, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(["AB01", "AB02"], (await db.ReadItemsAsync(product)).Select(i => i.Epc).Order());

        await db.MarkSoldAsync("AB02");
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.DeleteReceptionAsync(kept.Id!, db.Token));
        Assert.Equal(2, (await db.ReadItemsAsync(product)).Count);
        Assert.Single(await db.ReadReceptionsAsync(product));
    }

    [MongoReplicaSetFact]
    public async Task LegacyProductEpcsBecomeItemsUpToStockAndTheOldIndexIsDropped()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var stocked = await db.InsertLegacyProductAsync(1, "AAAA0001", stock: 3);
        var empty = await db.InsertLegacyProductAsync(2, "BBBB0002", stock: 0);

        await db.Products.BackfillCodesAsync(db.Token);
        await db.Products.BackfillCodesAsync(db.Token);

        Assert.DoesNotContain("ux_products_epc", await db.ReadProductIndexNamesAsync());
        Assert.False((await db.ReadRawProductAsync(stocked)).Contains("epc"));
        Assert.False((await db.ReadRawProductAsync(empty)).Contains("epc"));
        var item = await db.Products.FindItemAsync("aaaa0001", db.Token);
        Assert.Equal(stocked, item?.ProductId);
        Assert.Equal(Item.InStock, item?.Status);
        Assert.Null(await db.Products.FindItemAsync("BBBB0002", db.Token));
        Assert.Equal(3, (await db.Products.GetByIdAsync(stocked, db.Token))!.QuantityInStock);

        // Without the old unique index, products without an EPC no longer collide.
        await db.AddProductAsync(ordinal: 1);
        await db.AddProductAsync(ordinal: 2);
        Assert.Equal(4, (await db.ReadProductsAsync()).Count);
    }

    [MongoReplicaSetFact]
    public async Task ApprovedCheckoutPersistsAuthoritativeReceiptStockAndLoyaltyTogether()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 8, price: 12.75m);
        var customer = await db.AddCustomerAsync(points: 7);

        var sale = await db.Store.CheckoutAsync(
            new Dictionary<string, int> { [product.Upc] = 2 }, "approve", " CUSTOMER@EXAMPLE.TEST ", db.Token);

        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("approved", stored.PaymentStatus);
        Assert.Equal(25.50m, stored.Total);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(customer.Email, stored.CustomerEmail);
        Assert.Equal(25, stored.PointsEarned);
        Assert.Equal("pending", stored.EmailStatus);
        var line = Assert.Single(stored.Items);
        Assert.Equal(product.Upc, line.Code);
        Assert.Equal(product.Name, line.Name);
        Assert.Equal(product.Price, line.Price);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(6, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(32, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Equal(sale.Id, (await db.Store.FindSaleAsync(sale.Id, db.Token))!.Id);
    }

    [MongoReplicaSetFact]
    public async Task FirstCheckoutCreatesAccountAndReturningShopperKeepsPoints()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 2, price: 2.50m);
        var cart = new Dictionary<string, int> { [product.Upc] = 1 };
        var sale = await db.Store.CheckoutAsync(cart, "approve", " Guest@Example.test ", db.Token);
        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("guest@example.test", stored.CustomerEmail);
        Assert.Equal("pending", stored.EmailStatus);
        Assert.NotNull(stored.CustomerId);
        Assert.Equal(2, stored.PointsEarned);
        Assert.Equal(2, stored.PointsBalanceAfter);
        var log = Assert.Single(await db.Store.ListEmailsAsync(0, 25, db.Token));
        Assert.Equal(sale.Id, log.Id);
        Assert.Equal("pending", log.Status);
        await db.Store.UpdateEmailStatusAsync(sale.Id, "sent", db.Token);
        log = Assert.Single(await db.Store.ListEmailsAsync(0, 25, db.Token));
        Assert.Equal("sent", log.Status);
        Assert.NotNull(log.CompletedAtUtc);
        var declined = await db.Store.CheckoutAsync(cart, "decline", "guest@example.test", db.Token);
        Assert.Equal("not_requested", declined.EmailStatus);
        var second = await db.Store.CheckoutAsync(cart, "approve", "guest@example.test", db.Token);
        Assert.Equal(sale.CustomerId, second.CustomerId);
        Assert.Equal(4, second.PointsBalanceAfter);
        var customer = Assert.Single(await db.Customers.GetRecentAsync(25, db.Token));
        Assert.Equal(4, customer.TotalPoints);
        Assert.True(customer.CheckoutOnly);
        Assert.Equal(3, (await db.Store.ListTransactionsAsync(0, 25, db.Token)).Count);
    }

    [MongoReplicaSetFact]
    public async Task DeclinedCheckoutRecordsAttemptWithoutChangingStockOrLoyalty()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 8, price: 12.75m);
        var customer = await db.AddCustomerAsync(points: 7);

        var sale = await db.Store.CheckoutAsync(
            new Dictionary<string, int> { [product.Upc] = 2 }, "decline", customer.Email, db.Token);

        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("declined", stored.PaymentStatus);
        Assert.Equal(25.50m, stored.Total);
        Assert.Equal(customer.Id, stored.CustomerId);
        Assert.Equal(0, stored.PointsEarned);
        Assert.Equal("not_requested", stored.EmailStatus);
        Assert.Equal(8, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
    }

    [MongoReplicaSetFact]
    public async Task UpcAndEpcAliasesBecomeOneSaleLineAndOneCombinedStockDeduction()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 7, price: 3.25m);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 2, "E200F001", "E200F002", "E200F003"), db.Token);

        var sale = await db.Store.CheckoutAsync(new Dictionary<string, int>
        {
            [product.Upc] = 2,
            ["e200f001"] = 1,
            ["E200F002"] = 1,
            ["E200F003"] = 1
        }, "approve", null, db.Token);

        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        var line = Assert.Single(stored.Items);
        Assert.Equal(product.Upc, line.Code);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(16.25m, stored.Total);
        Assert.Null(stored.CustomerId);
        Assert.Equal(0, stored.PointsEarned);
        Assert.Equal(5, (await db.ReadProductAsync(product))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task MultiItemShortageLeavesEveryProductAndCustomerUnchanged()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var available = await db.AddProductAsync(stock: 8, ordinal: 1);
        var shortItem = await db.AddProductAsync(stock: 1, ordinal: 2);
        var customer = await db.AddCustomerAsync(points: 7);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.CheckoutAsync(
            new Dictionary<string, int> { [available.Upc] = 3, [shortItem.Upc] = 2 },
            "approve", customer.Email, db.Token));

        Assert.Equal(8, (await db.ReadProductAsync(available))!.QuantityInStock);
        Assert.Equal(1, (await db.ReadProductAsync(shortItem))!.QuantityInStock);
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Empty(await db.ReadSalesAsync());
    }

    [MongoReplicaSetFact]
    public async Task RejectedReceiptInsertRollsBackAllStockAndLoyaltyWrites()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(stock: 8, ordinal: 1);
        var second = await db.AddProductAsync(stock: 6, ordinal: 2);
        var customer = await db.AddCustomerAsync(points: 7);
        await db.RejectWritesAsync("demo_sales");

        await Assert.ThrowsAnyAsync<MongoException>(() => db.Store.CheckoutAsync(
            new Dictionary<string, int> { [first.Upc] = 2, [second.Upc] = 3 },
            "approve", customer.Email, db.Token));

        Assert.Equal(8, (await db.ReadProductAsync(first))!.QuantityInStock);
        Assert.Equal(6, (await db.ReadProductAsync(second))!.QuantityInStock);
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Empty(await db.ReadSalesAsync());
    }

    [MongoReplicaSetFact]
    public async Task RejectedReceptionInsertRollsBackTheStockIncrement()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 4);
        var openingHistory = Assert.Single(await db.ReadReceptionsAsync(product));
        await db.RejectWritesAsync("receptions");

        await Assert.ThrowsAnyAsync<MongoException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewReception(product, 7, day: 2), db.Token));

        Assert.Equal(4, (await db.ReadProductAsync(product))!.QuantityInStock);
        var history = Assert.Single(await db.ReadReceptionsAsync(product));
        Assert.Equal(openingHistory.Id, history.Id);
        Assert.Equal(4, history.Quantity);
    }

    [MongoReplicaSetFact]
    public async Task UndoAfterSomeUnitsWereSoldRetainsTheDeliveryAndRemainingStock()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 5);
        var reception = Assert.Single(await db.ReadReceptionsAsync(product));
        var sale = await db.Store.CheckoutAsync(
            new Dictionary<string, int> { [product.Upc] = 2 }, "approve", null, db.Token);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Products.DeleteReceptionAsync(reception.Id!, db.Token));

        Assert.Equal(3, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(reception.Id, Assert.Single(await db.ReadReceptionsAsync(product)).Id);
        Assert.Equal(sale.Id, Assert.Single(await db.ReadSalesAsync()).Id);
    }

    [MongoReplicaSetFact]
    public async Task ConcurrentUndoRemovesOneDeliveryAndDeductsItsStockExactlyOnce()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 9);
        var reception = Assert.Single(await db.ReadReceptionsAsync(product));

        var results = await RunTogetherAsync(
            () => db.Products.DeleteReceptionAsync(reception.Id!, db.Token),
            () => db.Products.DeleteReceptionAsync(reception.Id!, db.Token));

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        Assert.Equal(0, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Empty(await db.ReadReceptionsAsync(product));
        Assert.False(await db.Products.DeleteReceptionAsync(reception.Id!, db.Token));
        Assert.Equal(0, (await db.ReadProductAsync(product))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task ProductDeletionRemovesItsWholeHistoryButPreservesOtherProducts()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var deletedProduct = await db.AddProductAsync(stock: 5, ordinal: 1);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewReception(deletedProduct, 7, day: 2), db.Token);
        var retainedProduct = await db.AddProductAsync(stock: 4, ordinal: 2);
        var retainedDelivery = Assert.Single(await db.ReadReceptionsAsync(retainedProduct));

        Assert.True(await db.Products.DeleteAsync(deletedProduct.Id!, db.Token));

        Assert.Null(await db.ReadProductAsync(deletedProduct));
        Assert.Empty(await db.ReadReceptionsAsync(deletedProduct));
        Assert.Equal(4, (await db.ReadProductAsync(retainedProduct))!.QuantityInStock);
        Assert.Equal(retainedDelivery.Id, Assert.Single(await db.ReadReceptionsAsync(retainedProduct)).Id);
        Assert.False(await db.Products.DeleteAsync(deletedProduct.Id!, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task CancellationAfterProductDeleteRollsBackProductAndHistoryTogether()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 5);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewReception(product, 7, day: 2), db.Token);
        var originalHistory = await db.ReadReceptionsAsync(product);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(db.Token);
        db.Gate.ArmAfterProductWrite("delete");
        var deletion = Task.Run(() => db.Products.DeleteAsync(product.Id!, cancellation.Token));
        Exception? failure = null;
        try
        {
            await db.Gate.WaitUntilPausedAsync(db.Token);
            cancellation.Cancel();
        }
        finally
        {
            db.Gate.Release();
            failure = await Record.ExceptionAsync(async () => await deletion.WaitAsync(TimeSpan.FromSeconds(20)));
        }

        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.False(db.Gate.TimedOut, "The command gate timed out rather than being released by the test.");
        Assert.Equal(12, (await db.ReadProductAsync(product))!.QuantityInStock);
        var history = await db.ReadReceptionsAsync(product);
        Assert.Equal(originalHistory.Select(r => r.Id).OrderBy(id => id), history.Select(r => r.Id).OrderBy(id => id));
        Assert.Equal(12, history.Sum(r => r.Quantity));
    }

    [MongoReplicaSetFact]
    public async Task OutsiderCannotSeeAnyStockReservationBeforeTheWholeCheckoutCommits()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(stock: 8, ordinal: 1, price: 3m);
        var second = await db.AddProductAsync(stock: 6, ordinal: 2, price: 2m);
        var customer = await db.AddCustomerAsync(points: 7);
        db.Gate.ArmAfterProductWrite("update");
        var checkout = Task.Run(() => db.Store.CheckoutAsync(
            new Dictionary<string, int> { [first.Upc] = 2, [second.Upc] = 3 },
            "approve", customer.Email, db.Token));
        DemoSale? sale = null;
        try
        {
            await db.Gate.WaitUntilPausedAsync(db.Token);

            // The first stock write has succeeded on the server, but checkout is still paused.
            // Manual compensation cannot satisfy this: nontransactional reservation is already visible.
            Assert.Equal(8, (await db.ReadProductAsync(first))!.QuantityInStock);
            Assert.Equal(6, (await db.ReadProductAsync(second))!.QuantityInStock);
            Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
            Assert.Empty(await db.ReadSalesAsync());
        }
        finally
        {
            db.Gate.Release();
            sale = await checkout.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.False(db.Gate.TimedOut, "The command gate timed out rather than being released by the test.");
        Assert.Equal(6, (await db.ReadProductAsync(first))!.QuantityInStock);
        Assert.Equal(3, (await db.ReadProductAsync(second))!.QuantityInStock);
        Assert.Equal(19, (await db.ReadCustomerAsync(customer)).TotalPoints);
        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal(12m, stored.Total);
        Assert.Equal(12, stored.PointsEarned);
    }

    [MongoReplicaSetFact]
    public async Task ConcurrentCheckoutsCannotOversellOrAwardLoyaltyForTheLosingAttempt()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 5, price: 2.40m);
        var customer = await db.AddCustomerAsync(points: 7);

        async Task<(DemoSale? Sale, Exception? Failure)> AttemptAsync()
        {
            try
            {
                return (await db.Store.CheckoutAsync(new Dictionary<string, int> { [product.Upc] = 4 },
                    "approve", customer.Email, db.Token), null);
            }
            catch (Exception failure)
            {
                return (null, failure);
            }
        }

        var results = await RunTogetherAsync(AttemptAsync, AttemptAsync);

        var winner = Assert.Single(results, result => result.Failure is null);
        var loser = Assert.Single(results, result => result.Failure is not null);
        Assert.IsType<InvalidOperationException>(loser.Failure);
        Assert.Null(loser.Sale);
        Assert.Equal(1, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(16, (await db.ReadCustomerAsync(customer)).TotalPoints);
        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(winner.Sale!.Id, stored.Id);
        Assert.Equal(9.60m, stored.Total);
        Assert.Equal(9, stored.PointsEarned);
        Assert.Equal(4, Assert.Single(stored.Items).Quantity);
    }

    [MongoReplicaSetFact]
    public async Task CodesSurviveMigrationEditsAndCheckoutUntilProductDeletion()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(stock: 3);
        var other = await db.AddProductAsync(ordinal: 2);
        await db.Products.AssignCodeAsync(" long scanned/case-017 ", first.Upc, db.Token);
        await db.Products.AssignCodeAsync("aa00", first.Upc, db.Token);
        await Assert.ThrowsAsync<DuplicateProductCodeException>(() => db.Products.AssignCodeAsync("AA00", other.Upc, db.Token));
        // A registered RFID tag identifies one unit and can't become a barcode.
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(first, 2, "EE00"), db.Token);
        await Assert.ThrowsAsync<DuplicateProductCodeException>(() => db.Products.AssignCodeAsync("ee00", first.Upc, db.Token));
        first.Upc = "1999999999999";
        await db.Products.UpdateAsync(first, db.Token);
        Assert.Equal((first.Id, false), (await db.Products.ResolveCodeAsync("aa00", db.Token)) is { } alias ? (alias.Product.Id, alias.IsTag) : default);
        Assert.Equal((first.Id, true), (await db.Products.ResolveCodeAsync("ee00", db.Token)) is { } tag ? (tag.Product.Id, tag.IsTag) : default);
        await db.Store.BackfillCheckoutRecordsAsync(db.Token);
        await db.Store.BackfillCheckoutRecordsAsync(db.Token);
        var sale = await db.Store.CheckoutAsync(new() { ["long scanned/case-017"] = 1, ["AA00"] = 1 }, "approve", null, db.Token);
        Assert.Equal(2, Assert.Single(sale.Items).Quantity);
        await db.Products.DeleteAsync(first.Id!, db.Token);
        Assert.Null(await db.Products.ResolveCodeAsync("AA00", db.Token));
        Assert.Null(await db.Products.ResolveCodeAsync("EE00", db.Token));
    }

    [MongoReplicaSetFact]
    public async Task ConcurrentFirstPurchasesShareAccountAndRegistrationPreservesBalance()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(stock: 1, price: 4.50m);
        var second = await db.AddProductAsync(stock: 1, ordinal: 2, price: 3m);
        var sales = await RunTogetherAsync(
            () => db.Store.CheckoutAsync(new() { [first.Upc] = 1 }, "approve", "customer@example.test", db.Token),
            () => db.Store.CheckoutAsync(new() { [second.Upc] = 1 }, "approve", "CUSTOMER@example.test", db.Token));
        Assert.Equal(sales[0].CustomerId, sales[1].CustomerId);
        await db.AddCustomerAsync();
        var customer = Assert.Single(await db.Customers.GetRecentAsync(10, db.Token));
        Assert.Equal(sales[0].CustomerId, customer.Id);
        Assert.Equal(7, customer.TotalPoints);
        Assert.False(customer.CheckoutOnly);
    }

    private static async Task AssertConcurrentDuplicateAsync(string field)
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = MongoTestDatabase.NewProduct(ordinal: 1);
        var second = MongoTestDatabase.NewProduct(ordinal: 2);
        second.Upc = first.Upc;

        var failures = await RunTogetherAsync(
            () => Record.ExceptionAsync(() => db.Products.AddAsync(first, db.Token)),
            () => Record.ExceptionAsync(() => db.Products.AddAsync(second, db.Token)));

        Assert.Single(failures, failure => failure is null);
        var failure = Assert.Single(failures, error => error is not null);
        Assert.Equal(field, Assert.IsType<DuplicateProductCodeException>(failure).Field);
        var stored = Assert.Single(await db.ReadProductsAsync());
        Assert.Equal(first.Upc, stored.Upc);
        Assert.Equal(0, stored.QuantityInStock);
    }

    private static async Task<T[]> RunTogetherAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = Task.Run(async () =>
        {
            await start.Task;
            return await first();
        });
        var secondTask = Task.Run(async () =>
        {
            await start.Task;
            return await second();
        });
        start.SetResult(true);
        return await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(45));
    }
}
