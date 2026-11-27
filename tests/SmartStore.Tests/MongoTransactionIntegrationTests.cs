using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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
        Assert.True((await db.ReadRawProductAsync(stocked)).Contains("epc"));
        Assert.True((await db.ReadRawProductAsync(empty)).Contains("epc"));
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
        await db.AddBalanceAsync(7);

        var sale = await db.Store.CheckoutAsync(
            new Dictionary<string, int> { [product.Upc] = 2 }, "approve", " CUSTOMER@EXAMPLE.TEST ", db.NewAttempt(), db.Token);

        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("approved", stored.PaymentStatus);
        Assert.Equal(25.50m, stored.Total);
        Assert.Null(stored.CustomerId);
        Assert.Equal(MongoTestDatabase.MemberId, stored.AccountId);
        Assert.Equal(customer.Email, stored.CustomerEmail);
        Assert.Equal(25, stored.PointsEarned);
        Assert.Equal("pending", stored.EmailStatus);
        var line = Assert.Single(stored.Items);
        Assert.Equal(product.Upc, line.Code);
        Assert.Equal(product.Name, line.Name);
        Assert.Equal(product.Id, line.ProductId);
        Assert.Equal("entered_identifier", line.IdentifierSource);
        Assert.Equal(product.Price, line.Price);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(6, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(32, await db.ReadBalanceAsync());
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Equal(sale.Id, (await db.Store.FindSaleAsync(sale.Id, db.Token))!.Id);
    }

    [MongoReplicaSetFact]
    public async Task MemberCheckoutCreatesSeparateBalanceAndNeverCreatesRegistryCustomerByEmail()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 2, price: 2.50m);
        var cart = new Dictionary<string, int> { [product.Upc] = 1 };
        var sale = await db.Store.CheckoutAsync(cart, "approve", " Guest@Example.test ", db.NewAttempt(), db.Token);
        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("guest@example.test", stored.CustomerEmail);
        Assert.Equal("pending", stored.EmailStatus);
        Assert.Null(stored.CustomerId);
        Assert.Equal(MongoTestDatabase.MemberId, stored.AccountId);
        Assert.Equal(2, stored.PointsEarned);
        Assert.Equal(2, stored.PointsBalanceAfter);
        var log = Assert.Single(await db.Store.ListEmailsAsync(0, 25, db.Token));
        Assert.Equal(sale.Id, log.Id);
        Assert.Equal("pending", log.Status);
        Assert.True(await db.Store.ClaimReceiptEmailAsync(sale.Id, db.Token));
        await db.Store.UpdateEmailStatusAsync(sale.Id, "sent", db.Token);
        log = Assert.Single(await db.Store.ListEmailsAsync(0, 25, db.Token));
        Assert.Equal("sent", log.Status);
        Assert.NotNull(log.CompletedAtUtc);
        var declined = await db.Store.CheckoutAsync(cart, "decline", "guest@example.test", db.NewAttempt(), db.Token);
        Assert.Equal("not_requested", declined.EmailStatus);
        var second = await db.Store.CheckoutAsync(cart, "approve", "guest@example.test", db.NewAttempt(), db.Token);
        Assert.Equal(sale.AccountId, second.AccountId);
        Assert.Equal(4, second.PointsBalanceAfter);
        Assert.Equal(4, await db.ReadBalanceAsync());
        Assert.Empty(await db.Customers.GetRecentAsync(25, db.Token));
        Assert.Equal(3, (await db.Store.ListTransactionsAsync(0, 25, db.Token)).Count);
    }

    [MongoReplicaSetFact]
    public async Task DeclinedCheckoutRecordsAttemptWithoutChangingStockOrLoyalty()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 8, price: 12.75m);
        var customer = await db.AddCustomerAsync(points: 7);
        await db.AddBalanceAsync(7);

        var sale = await db.Store.CheckoutAsync(
            new Dictionary<string, int> { [product.Upc] = 2 }, "decline", customer.Email, db.NewAttempt(), db.Token);

        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("declined", stored.PaymentStatus);
        Assert.Equal(25.50m, stored.Total);
        Assert.Null(stored.CustomerId);
        Assert.Equal(MongoTestDatabase.MemberId, stored.AccountId);
        Assert.Equal(0, stored.PointsEarned);
        Assert.Equal("not_requested", stored.EmailStatus);
        Assert.Equal(8, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Equal(7, await db.ReadBalanceAsync());
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
        }, "approve", null, db.NewAttempt(null), db.Token);

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
    public async Task BarcodeCheckoutMarksTheOldestTagsSoldAndDeclinedCheckoutLeavesThemInStock()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 3, "B003"), db.Token);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "A002", "A001"), db.Token);

        await db.Store.CheckoutAsync(new() { [product.Upc] = 2 }, "decline", null, db.NewAttempt(null), db.Token);
        Assert.All(await db.ReadItemsAsync(product), item => Assert.Equal(Item.InStock, item.Status));

        var sale = await db.Store.CheckoutAsync(new() { [product.Upc] = 2 }, "approve", null, db.NewAttempt(null), db.Token);

        var items = (await db.ReadItemsAsync(product)).ToDictionary(i => i.Epc);
        Assert.Equal((Item.Sold, sale.Id), (items["A001"].Status, items["A001"].SaleId));
        Assert.Equal((Item.Sold, sale.Id), (items["A002"].Status, items["A002"].SaleId));
        Assert.NotNull(items["A001"].SoldAtUtc);
        Assert.Equal((Item.InStock, (string?)null), (items["B003"].Status, items["B003"].SaleId));
        Assert.Equal(1, (await db.ReadProductAsync(product))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task ScannedTagIsTheUnitSoldAndCannotBeSoldOrReceivedAgain()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "A001", "A002", "A003"), db.Token);

        // The newest-scanned tag plus one barcode unit: A003 exactly, then the oldest remaining (A001).
        var sale = await db.Store.CheckoutAsync(new() { ["a003"] = 1, [product.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token);

        var items = (await db.ReadItemsAsync(product)).ToDictionary(i => i.Epc);
        Assert.Equal(sale.Id, items["A003"].SaleId);
        Assert.Equal(sale.Id, items["A001"].SaleId);
        Assert.Equal(Item.InStock, items["A002"].Status);
        Assert.Equal(1, (await db.ReadProductAsync(product))!.QuantityInStock);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Store.CheckoutAsync(new() { ["A003"] = 1 }, "approve", null, db.NewAttempt(null), db.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Store.CheckoutAsync(new() { ["A002"] = 2 }, "approve", null, db.NewAttempt(null), db.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(product, 2, "A003"), db.Token));
        Assert.Equal(1, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Single(await db.ReadSalesAsync());
    }

    [MongoReplicaSetFact]
    public async Task ScanningATagABarcodeSaleGuessedMovesTheGuessToAnotherUnit()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "A001", "A002", "A003"), db.Token);

        // Customer A buys one by barcode; the system guesses the oldest tag, A001.
        var first = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        var items = (await db.ReadItemsAsync(product)).ToDictionary(i => i.Epc);
        Assert.Equal((Item.Sold, Item.SoldByGuess, first.Id), (items["A001"].Status, items["A001"].SoldBy, items["A001"].SaleId));

        // Customer B scans the real A001: B gets A001, and A's guess moves to the oldest unit left, A002.
        var second = await db.Store.CheckoutAsync(new() { ["A001"] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        items = (await db.ReadItemsAsync(product)).ToDictionary(i => i.Epc);
        Assert.Equal((Item.Sold, Item.SoldByScan, second.Id), (items["A001"].Status, items["A001"].SoldBy, items["A001"].SaleId));
        Assert.Equal((Item.Sold, Item.SoldByGuess, first.Id), (items["A002"].Status, items["A002"].SoldBy, items["A002"].SaleId));
        Assert.Equal(Item.InStock, items["A003"].Status);
        Assert.Equal(1, (await db.ReadProductAsync(product))!.QuantityInStock);

        // A confirmed sale is final: scanning A001 again is refused.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Store.CheckoutAsync(new() { ["A001"] = 1 }, "approve", null, db.NewAttempt(null), db.Token));

        // Selling A003 by scan leaves no stock. A002 still carries A's guess, and with nothing left to move it
        // to, scanning A002 is refused rather than selling more units than were received.
        await db.Store.CheckoutAsync(new() { ["A003"] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        Assert.Equal(0, (await db.ReadProductAsync(product))!.QuantityInStock);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Store.CheckoutAsync(new() { ["A002"] = 1 }, "approve", null, db.NewAttempt(null), db.Token));
    }

    [MongoReplicaSetFact]
    public async Task ReceivingATagABarcodeSaleGuessedPutsItBackWithoutAddingStock()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "A001", "A002"), db.Token);
        var sale = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token);

        // A001 was guessed but is still on the shelf; rescanning it alongside a new tag corrects the guess.
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 2, "A001", "B001"), db.Token);

        var items = (await db.ReadItemsAsync(product)).ToDictionary(i => i.Epc);
        Assert.Equal(Item.InStock, items["A001"].Status);
        Assert.Null(items["A001"].SaleId);
        Assert.Equal((Item.Sold, sale.Id), (items["A002"].Status, items["A002"].SaleId));
        Assert.Equal(Item.InStock, items["B001"].Status);
        Assert.Equal(2, (await db.ReadProductAsync(product))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task MultiItemShortageLeavesEveryProductAndCustomerUnchanged()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var available = await db.AddProductAsync(stock: 8, ordinal: 1);
        var shortItem = await db.AddProductAsync(stock: 1, ordinal: 2);
        var customer = await db.AddCustomerAsync(points: 7);
        await db.AddBalanceAsync(7);

        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.CheckoutAsync(
            new Dictionary<string, int> { [available.Upc] = 3, [shortItem.Upc] = 2 },
            "approve", customer.Email, db.NewAttempt(), db.Token));

        Assert.Equal(8, (await db.ReadProductAsync(available))!.QuantityInStock);
        Assert.Equal(1, (await db.ReadProductAsync(shortItem))!.QuantityInStock);
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Equal(7, await db.ReadBalanceAsync());
        Assert.Empty(await db.ReadSalesAsync());
    }

    [MongoReplicaSetFact]
    public async Task RejectedReceiptInsertRollsBackAllStockAndLoyaltyWrites()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(stock: 8, ordinal: 1);
        var second = await db.AddProductAsync(stock: 6, ordinal: 2);
        var customer = await db.AddCustomerAsync(points: 7);
        await db.AddBalanceAsync(7);
        await db.RejectWritesAsync("demo_sales");

        await Assert.ThrowsAnyAsync<MongoException>(() => db.Store.CheckoutAsync(
            new Dictionary<string, int> { [first.Upc] = 2, [second.Upc] = 3 },
            "approve", customer.Email, db.NewAttempt(), db.Token));

        Assert.Equal(8, (await db.ReadProductAsync(first))!.QuantityInStock);
        Assert.Equal(6, (await db.ReadProductAsync(second))!.QuantityInStock);
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        Assert.Equal(7, await db.ReadBalanceAsync());
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
            new Dictionary<string, int> { [product.Upc] = 2 }, "approve", null, db.NewAttempt(null), db.Token);

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
    public async Task ProductArchiveRetainsStockItemsCodesAndHistoryButStopsSales()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var deletedProduct = await db.AddProductAsync(stock: 5, ordinal: 1);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewReception(deletedProduct, 7, day: 2), db.Token);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(deletedProduct, 3, "AC01"), db.Token);
        var retainedProduct = await db.AddProductAsync(stock: 4, ordinal: 2);
        var retainedDelivery = Assert.Single(await db.ReadReceptionsAsync(retainedProduct));

        Assert.True(await db.Products.ArchiveAsync(deletedProduct.Id!, db.Token));

        Assert.NotNull((await db.ReadProductAsync(deletedProduct))!.ArchivedAtUtc);
        Assert.Equal(13, (await db.ReadProductAsync(deletedProduct))!.QuantityInStock);
        Assert.Equal(3, (await db.ReadReceptionsAsync(deletedProduct)).Count);
        Assert.Single(await db.ReadItemsAsync(deletedProduct));
        Assert.Null(await db.Products.ResolveCodeAsync(deletedProduct.Upc, db.Token));
        Assert.Null(await db.Products.ResolveCodeAsync("AC01", db.Token));
        Assert.DoesNotContain(await db.Store.GetProductsAsync(db.Token), p => p.ProductId == deletedProduct.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.CheckoutAsync(
            new() { [deletedProduct.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token));
        await Assert.ThrowsAsync<ProductNotFoundException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewReception(deletedProduct, 1), db.Token));
        Assert.Equal(4, (await db.ReadProductAsync(retainedProduct))!.QuantityInStock);
        Assert.Equal(retainedDelivery.Id, Assert.Single(await db.ReadReceptionsAsync(retainedProduct)).Id);
        Assert.False(await db.Products.ArchiveAsync(deletedProduct.Id!, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task CancellationAfterProductArchiveRollsBackAndRetainsHistory()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 5);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewReception(product, 7, day: 2), db.Token);
        var originalHistory = await db.ReadReceptionsAsync(product);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(db.Token);
        db.Gate.ArmAfterProductWrite("update");
        var deletion = Task.Run(() => db.Products.ArchiveAsync(product.Id!, cancellation.Token));
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
        Assert.Null((await db.ReadProductAsync(product))!.ArchivedAtUtc);
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
        await db.AddBalanceAsync(7);
        db.Gate.ArmAfterProductWrite("update");
        var checkout = Task.Run(() => db.Store.CheckoutAsync(
            new Dictionary<string, int> { [first.Upc] = 2, [second.Upc] = 3 },
            "approve", customer.Email, db.NewAttempt(), db.Token));
        DemoSale? sale = null;
        try
        {
            await db.Gate.WaitUntilPausedAsync(db.Token);

            // The first stock write has succeeded on the server, but checkout is still paused.
            // Manual compensation cannot satisfy this: nontransactional reservation is already visible.
            Assert.Equal(8, (await db.ReadProductAsync(first))!.QuantityInStock);
            Assert.Equal(6, (await db.ReadProductAsync(second))!.QuantityInStock);
            Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
            Assert.Equal(7, await db.ReadBalanceAsync());
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
        Assert.Equal(19, await db.ReadBalanceAsync());
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
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
        await db.AddBalanceAsync(7);

        async Task<(DemoSale? Sale, Exception? Failure)> AttemptAsync()
        {
            try
            {
                return (await db.Store.CheckoutAsync(new Dictionary<string, int> { [product.Upc] = 4 },
                    "approve", customer.Email, db.NewAttempt(), db.Token), null);
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
        Assert.Equal(16, await db.ReadBalanceAsync());
        Assert.Equal(7, (await db.ReadCustomerAsync(customer)).TotalPoints);
        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(winner.Sale!.Id, stored.Id);
        Assert.Equal(9.60m, stored.Total);
        Assert.Equal(9, stored.PointsEarned);
        Assert.Equal(4, Assert.Single(stored.Items).Quantity);
    }

    [MongoReplicaSetFact]
    public async Task CodesSurviveMigrationEditsAndRemainReservedAfterProductArchive()
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
        var sale = await db.Store.CheckoutAsync(new() { ["long scanned/case-017"] = 1, ["AA00"] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        Assert.Equal(2, Assert.Single(sale.Items).Quantity);
        await db.Products.ArchiveAsync(first.Id!, db.Token);
        Assert.Null(await db.Products.ResolveCodeAsync("AA00", db.Token));
        Assert.Null(await db.Products.ResolveCodeAsync("EE00", db.Token));
        Assert.NotNull(await db.Products.FindItemAsync("EE00", db.Token));
        await Assert.ThrowsAsync<DuplicateProductCodeException>(() => db.Products.AssignCodeAsync("AA00", other.Upc, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task ConcurrentFirstPurchasesShareAccountBalanceButNeverMergeLegacyRegistry()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = await db.AddProductAsync(stock: 1, price: 4.50m);
        var second = await db.AddProductAsync(stock: 1, ordinal: 2, price: 3m);
        var sales = await RunTogetherAsync(
            () => db.Store.CheckoutAsync(new() { [first.Upc] = 1 }, "approve", "customer@example.test", db.NewAttempt(), db.Token),
            () => db.Store.CheckoutAsync(new() { [second.Upc] = 1 }, "approve", "CUSTOMER@example.test", db.NewAttempt(), db.Token));
        Assert.All(sales, sale => Assert.Equal(MongoTestDatabase.MemberId, sale.AccountId));
        Assert.All(sales, sale => Assert.Null(sale.CustomerId));
        Assert.Equal(7, await db.ReadBalanceAsync());
        await db.AddCustomerAsync();
        var customer = Assert.Single(await db.Customers.GetRecentAsync(10, db.Token));
        Assert.NotEqual(MongoTestDatabase.MemberId, customer.Id);
        Assert.Equal(7, customer.TotalPoints);
        Assert.False(customer.CheckoutOnly);
    }

    [MongoReplicaSetFact]
    public async Task OneLogicalCheckoutReturnsOneReceiptAcrossConcurrentAndChangedRetries()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 4, price: 10m);
        await db.AddBalanceAsync(250);
        await db.Store.RedeemCouponAsync(MongoTestDatabase.MemberId, 10, db.Token);
        var coupon = Assert.Single(await db.Store.GetCouponsAsync(MongoTestDatabase.MemberId, db.Token));
        var attempt = db.NewAttempt();
        var cart = new Dictionary<string, int> { [product.Upc] = 2 };
        var results = await RunTogetherAsync(
            () => db.Store.CheckoutAsync(cart, "approve", "member@example.test", attempt, db.Token, couponId: coupon.Id),
            () => db.Store.CheckoutAsync(cart, "approve", "member@example.test", attempt, db.Token, couponId: coupon.Id));
        Assert.Equal(results[0].Id, results[1].Id);
        Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(2, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(18, await db.ReadBalanceAsync());
        Assert.Empty(await db.Store.GetCouponsAsync(MongoTestDatabase.MemberId, db.Token));
        Assert.Single(await db.Store.ListEmailsAsync(0, 100, db.Token));
        var claims = await RunTogetherAsync(
            () => db.Store.ClaimReceiptEmailAsync(results[0].Id, db.Token),
            () => db.Store.ClaimReceiptEmailAsync(results[0].Id, db.Token));
        Assert.Single(claims, claimed => claimed);
        Assert.Single(claims, claimed => !claimed);
        await db.Store.UpdateEmailStatusAsync(results[0].Id, "sent", db.Token);
        await db.Products.ArchiveAsync(product.Id!, db.Token);
        var retry = await db.Store.CheckoutAsync(new(), "decline", null, attempt, db.Token);
        Assert.Equal(results[0].Id, retry.Id);
        Assert.Equal("approved", retry.PaymentStatus);
        Assert.Equal("sent", retry.EmailStatus);
        Assert.False(await db.Store.ClaimReceiptEmailAsync(retry.Id, db.Token));
        Assert.Equal(18, await db.ReadBalanceAsync());
    }

    [MongoReplicaSetFact]
    public async Task AttemptIdentifiersAreBoundToCallerAndDeclinesAreAlsoIdempotent()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 4, price: 3m);
        var cart = new Dictionary<string, int> { [product.Upc] = 1 };
        var attemptId = Guid.NewGuid().ToString("N");
        var first = db.NewAttempt(attemptId: attemptId);
        var declined = await db.Store.CheckoutAsync(cart, "decline", null, first, db.Token);
        var retry = await db.Store.CheckoutAsync(cart, "approve", null, first, db.Token);
        Assert.Equal(declined.Id, retry.Id);
        Assert.Equal("declined", retry.PaymentStatus);
        const string otherAccount = "22222222222222222222222222222222";
        var other = await db.Store.CheckoutAsync(cart, "approve", null,
            db.NewAttempt(otherAccount, attemptId), db.Token);
        Assert.NotEqual(declined.Id, other.Id);
        Assert.Equal(2, (await db.ReadSalesAsync()).Count);
        Assert.Equal(3, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(0, await db.ReadBalanceAsync());
        Assert.Equal(3, await db.ReadBalanceAsync(otherAccount));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.CheckoutAsync(cart, "approve", null,
            new CheckoutAttempt(attemptId, "account:someone-else", MongoTestDatabase.MemberId), db.Token));
    }

    [MongoReplicaSetFact]
    public async Task GuestReceiptContactNeverClaimsLegacyHistoryPointsOrEmailOnlyCoupons()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 3, price: 2m);
        var legacy = await db.AddCustomerAsync(points: 700);
        var legacySale = new DemoSale { CustomerEmail = legacy.Email, CustomerId = legacy.Id, Total = 100m,
            Items = [new(product.Upc, "Historical name", 100m, 1)] };
        await db.InsertSalesAsync(legacySale);
        var legacyCoupon = new LoyaltyCoupon(Guid.NewGuid().ToString("N"), legacy.Email, 25);
        await db.InsertCouponAsync(legacyCoupon);
        await db.Store.BackfillCheckoutRecordsAsync(db.Token);
        Assert.Empty((await db.Store.CustomerHistoryPageAsync(MongoTestDatabase.MemberId, 1, 10, null, null, null, db.Token)).Sales);
        Assert.Equal(0m, await db.Store.CustomerTotalSpentAsync(MongoTestDatabase.MemberId, db.Token));
        Assert.Equal(0, await db.Store.CustomerPointsAsync(MongoTestDatabase.MemberId, db.Token));
        Assert.Empty(await db.Store.GetCouponsAsync(MongoTestDatabase.MemberId, db.Token));
        var attempt = db.NewAttempt(null);
        var sale = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", legacy.Email, attempt, db.Token);
        Assert.Null(sale.AccountId);
        Assert.Null(sale.CustomerId);
        Assert.Equal(StoreDemo.Hash(attempt.OwnerId), sale.ReceiptOwner);
        Assert.Equal(0, sale.PointsEarned);
        Assert.Null(sale.PointsBalanceAfter);
        Assert.Equal("pending", sale.EmailStatus);
        Assert.Equal(700, (await db.ReadCustomerAsync(legacy)).TotalPoints);
        Assert.Null((await db.Store.FindSaleAsync(legacySale.Id, db.Token))!.AccountId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.CheckoutAsync(
            new() { [product.Upc] = 1 }, "approve", legacy.Email, db.NewAttempt(), db.Token, couponId: legacyCoupon.Id));
        Assert.Equal(2, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(2, (await db.ReadSalesAsync()).Count);
    }

    [MongoReplicaSetFact]
    public async Task BarcodeUnitsConsumeUntaggedStockBeforeRetiringAnyTaggedUnit()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 2);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 2, "AA01", "AA02"), db.Token);
        await db.Store.CheckoutAsync(new() { [product.Upc] = 2 }, "approve", null, db.NewAttempt(null), db.Token);
        Assert.All(await db.ReadItemsAsync(product), item => Assert.Equal(Item.InStock, item.Status));
        var sale = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        var items = (await db.ReadItemsAsync(product)).ToDictionary(item => item.Epc);
        Assert.Equal((Item.Sold, Item.SoldByGuess, sale.Id), (items["AA01"].Status, items["AA01"].SoldBy, items["AA01"].SaleId));
        Assert.Equal(Item.InStock, items["AA02"].Status);
        Assert.Equal(1, (await db.ReadProductAsync(product))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task CorrectionOnlyReceivingCommitsWithoutInventingADeliveryOrMoreStock()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "AA01", "AA02"), db.Token);
        var sale = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        var correction = MongoTestDatabase.NewTaggedReception(product, 2, "AA01");
        var corrected = await db.Products.ReceiveStockAsync(correction, db.Token);
        Assert.Equal(0, correction.Quantity);
        Assert.Empty(correction.Epcs);
        Assert.Equal(1, corrected.QuantityInStock);
        Assert.Single(await db.ReadReceptionsAsync(product));
        var items = (await db.ReadItemsAsync(product)).ToDictionary(item => item.Epc);
        Assert.Equal(Item.InStock, items["AA01"].Status);
        Assert.Null(items["AA01"].SaleId);
        Assert.Equal((Item.Sold, sale.Id), (items["AA02"].Status, items["AA02"].SaleId));
        var saved = await db.Store.FindSaleAsync(sale.Id, db.Token);
        Assert.Equal(sale.Total, saved!.Total);
        Assert.Equal(sale.Items.Select(i => (i.ProductId, i.Code, i.Name, i.Price, i.Quantity)),
            saved.Items.Select(i => (i.ProductId, i.Code, i.Name, i.Price, i.Quantity)));
    }

    [MongoReplicaSetFact]
    public async Task LaterTaggedAndUntaggedDeliveriesCannotReplaceAnEarlierGuessedSale()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "AA01"), db.Token);
        var sale = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        var later = MongoTestDatabase.NewTaggedReception(product, 2, "BB01");
        later.CreatedAtUtc = sale.CreatedAtUtc.AddMinutes(1);
        await db.Products.ReceiveStockAsync(later, db.Token);
        var untagged = MongoTestDatabase.NewReception(product, 1, 3);
        untagged.CreatedAtUtc = sale.CreatedAtUtc.AddMinutes(2);
        await db.Products.ReceiveStockAsync(untagged, db.Token);
        Assert.False(await db.Products.CanCheckoutItemAsync(await db.Products.FindItemAsync("AA01", db.Token), db.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.CheckoutAsync(
            new() { ["AA01"] = 1 }, "approve", null, db.NewAttempt(null), db.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(product, 4, "AA01", "CC01"), db.Token));
        Assert.Null(await db.Products.FindItemAsync("CC01", db.Token));
        Assert.Equal(2, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Equal(sale.Id, (await db.Products.FindItemAsync("AA01", db.Token))!.SaleId);
        Assert.Equal(Item.InStock, (await db.Products.FindItemAsync("BB01", db.Token))!.Status);
    }

    [MongoReplicaSetFact]
    public async Task EpcLookupRejectsSoldUnknownAndBarcodeOnlyIdentifiersBeforeCheckout()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 1);
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(product, 1, "AA01"), db.Token);
        Assert.True(await db.Products.CanCheckoutItemAsync(await db.Products.FindItemAsync("AA01", db.Token), db.Token));
        var sale = await db.Store.CheckoutAsync(new() { ["AA01"] = 1 }, "approve", null, db.NewAttempt(null), db.Token);
        Assert.Equal(["AA01"], Assert.Single(sale.Items).Epcs);
        Assert.Equal("entered_identifier", Assert.Single(sale.Items).IdentifierSource);
        Assert.False(await db.Products.CanCheckoutItemAsync(await db.Products.FindItemAsync("AA01", db.Token), db.Token));
        Assert.False(await db.Products.CanCheckoutItemAsync(await db.Products.FindItemAsync(product.Upc, db.Token), db.Token));
        Assert.False(await db.Products.CanCheckoutItemAsync(await db.Products.FindItemAsync("FFFF", db.Token), db.Token));
    }

    [MongoReplicaSetFact]
    public async Task ProductIdentifiersKeepReportsAndPriceHistoryTogetherAfterUpcEdits()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 3, price: 2m);
        var originalUpc = product.Upc;
        var first = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(), db.Token);
        product.Upc = "1999999999999";
        product.Price = 3m;
        await db.Products.UpdateAsync(product, db.Token);
        var second = await db.Store.CheckoutAsync(new() { [product.Upc] = 1 }, "approve", null, db.NewAttempt(), db.Token);
        var start = DateTime.UtcNow.Date.AddDays(-1);
        var end = start.AddDays(3);
        var report = await db.Store.ReportAsync(start, end, db.Token);
        Assert.Equal(5m, report.Revenue);
        var grouped = Assert.Single(report.Products);
        Assert.Equal(product.Id, grouped.ProductKey);
        Assert.Equal(product.Upc, grouped.Code);
        Assert.Equal(2, grouped.Units);
        Assert.Equal(1, report.Customers);
        Assert.Equal(0, report.ReturningCustomers);
        Assert.Equal(5m, (await db.Store.CustomerSpendingAsync(MongoTestDatabase.MemberId, start, end, db.Token)).Sum(b => b.Total));
        var page = await db.Store.CustomerHistoryPageAsync(MongoTestDatabase.MemberId, 1, 10, product.Upc, null, null, db.Token);
        Assert.Equal(2, page.Count);
        Assert.Equal(product.Id, page.MatchedProductId);
        Assert.Equal(5m, page.Total);
        Assert.All(page.Sales, s => Assert.Equal(product.Id, Assert.Single(s.Items).ProductId));
        Assert.Equal(originalUpc, Assert.Single((await db.Store.FindSaleAsync(first.Id, db.Token))!.Items).Code);
        Assert.Equal(2m, Assert.Single((await db.Store.FindSaleAsync(first.Id, db.Token))!.Items).Price);
        Assert.Equal(product.Upc, Assert.Single(second.Items).Code);
    }

    [MongoReplicaSetFact]
    public async Task HistoryPagesAndAggregatesPreserveEveryReceiptAndExactTotals()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var receipts = Enumerable.Range(1, 21).Select(i => new DemoSale
        {
            AccountId = MongoTestDatabase.MemberId, CreatedAtUtc = start.AddHours(i), Total = i + 0.25m,
            Items = [new DemoSaleLine("old-upc", "Historical product", i + 0.25m, 1) { ProductId = "stable-product" }]
        }).ToArray();
        await db.InsertSalesAsync(receipts);
        await db.InsertSalesAsync(
            new DemoSale { AccountId = "22222222222222222222222222222222", CreatedAtUtc = start, Total = 50m },
            new DemoSale { AccountId = MongoTestDatabase.MemberId, CreatedAtUtc = start, Total = 1000m, PaymentStatus = "declined" });
        var read = new List<DemoSale>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await db.Store.CustomerHistoryPageAsync(MongoTestDatabase.MemberId, page, 10, null, null, null, db.Token);
            Assert.Equal(21, result.Count);
            Assert.Equal(236.25m, result.Total);
            Assert.InRange(result.Sales.Count, 1, 10);
            read.AddRange(result.Sales);
        }
        Assert.Equal(21, read.Select(sale => sale.Id).Distinct().Count());
        Assert.Equal(receipts.Select(sale => sale.Id).Order(), read.Select(sale => sale.Id).Order());
        Assert.Equal(236.25m, await db.Store.CustomerTotalSpentAsync(MongoTestDatabase.MemberId, db.Token));
        var buckets = await db.Store.CustomerSpendingAsync(MongoTestDatabase.MemberId, start, start.AddDays(2), db.Token);
        Assert.Equal(236.25m, buckets.Sum(bucket => bucket.Total));
        Assert.All(buckets, bucket => Assert.Equal(DateTimeKind.Utc, bucket.StartUtc.Kind));
        var report = await db.Store.ReportAsync(start, start.AddDays(2), db.Token);
        Assert.Equal(286.25m, report.Revenue);
        Assert.Equal(21, Assert.Single(report.Products).Units);
        Assert.Equal(2, report.Customers);
        await Assert.ThrowsAsync<ArgumentException>(() => db.Store.ReportAsync(start, start.AddDays(368), db.Token));
    }

    [MongoReplicaSetFact]
    public async Task UpcCreationAndEpcReceivingAtomicallyCompeteForTheSameIdentifier()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var stocked = await db.AddProductAsync(ordinal: 1);
        var competing = MongoTestDatabase.NewProduct(ordinal: 2);
        competing.Upc = "123456789012";
        var failures = await RunTogetherAsync(
            () => Record.ExceptionAsync(() => db.Products.AddAsync(competing, db.Token)),
            () => Record.ExceptionAsync(() => db.Products.ReceiveStockAsync(
                MongoTestDatabase.NewTaggedReception(stocked, 1, competing.Upc), db.Token)));
        Assert.Single(failures, failure => failure is null);
        Assert.Single(failures, failure => failure is DuplicateProductCodeException or InvalidOperationException);
        var item = await db.Products.FindItemAsync(competing.Upc, db.Token);
        var barcodeProduct = (await db.ReadProductsAsync()).SingleOrDefault(p => p.Upc == competing.Upc);
        Assert.NotEqual(item is null, barcodeProduct is null);
        Assert.Equal(item is null ? 0 : 1, (await db.ReadProductAsync(stocked))!.QuantityInStock);
    }

    [MongoReplicaSetFact]
    public async Task UpcEditsAndBarcodeAliasesCannotClaimAnExistingItemEpc()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var tagged = await db.AddProductAsync(ordinal: 1);
        var other = await db.AddProductAsync(ordinal: 2);
        const string epc = "123456789012";
        await db.Products.ReceiveStockAsync(MongoTestDatabase.NewTaggedReception(tagged, 1, epc), db.Token);
        var original = other.Upc;
        other.Upc = epc;
        await Assert.ThrowsAsync<DuplicateProductCodeException>(() => db.Products.UpdateAsync(other, db.Token));
        Assert.Equal(original, (await db.ReadProductAsync(other))!.Upc);
        await Assert.ThrowsAsync<DuplicateProductCodeException>(() => db.Products.AssignCodeAsync(epc, original, db.Token));
        Assert.Equal((tagged.Id, true), (await db.Products.ResolveCodeAsync(epc, db.Token)) is { } found
            ? (found.Product.Id, found.IsTag) : default);
    }

    [Theory]
    [InlineData("aabb", "AABB")]
    [InlineData(" E2801191A503006599BFD10D ", "E2801191A503006599BFD10D")]
    [InlineData("ABC", null)]
    [InlineData("ABCDE", null)]
    [InlineData("00112233445566778899AABBCC", null)]
    [InlineData("not-a-tag", null)]
    public void EpcValidationAcceptsOnlyCompleteHexBytesUpToTwentyFourCharacters(string entered, string? expected) =>
        Assert.Equal(expected, Item.NormalizeEpc(entered));

    [Fact]
    public void ReceiptOwnershipUsesAccountIdOrGuestCookieNeverMatchingEmail()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, MongoTestDatabase.MemberId),
            new Claim(ClaimTypes.Email, "reassigned@example.test")], "test"));
        var memberSale = new DemoSale { AccountId = MongoTestDatabase.MemberId, CustomerEmail = "old@example.test" };
        Assert.True(CheckoutAccess.Owns(context, memberSale));
        Assert.False(CheckoutAccess.Owns(context, new DemoSale { CustomerEmail = "reassigned@example.test" }));
        Assert.False(CheckoutAccess.Owns(context, new DemoSale { AccountId = "other-account", CustomerEmail = "reassigned@example.test" }));
        var cookie = new string('A', 64);
        context.Request.Headers.Cookie = "northstar.checkout-owner=" + cookie;
        Assert.True(CheckoutAccess.Owns(context, new DemoSale { ReceiptOwner = StoreDemo.Hash("guest:" + cookie) }));
        Assert.False(CheckoutAccess.Owns(new DefaultHttpContext(), new DemoSale { ReceiptOwner = StoreDemo.Hash("guest:" + cookie) }));
    }

    [Fact]
    public void OperationalMailSubjectsAndRecipientAddressesDoNotExposeApprovalMaterial()
    {
        var email = new EmailLog { Kind = "temperature-alert", Recipient = "customer@example.test",
            Subject = "Frig1 [Northstar:secret-approval]" };
        Assert.DoesNotContain("secret-approval", SmartStore.Pages.RecordsModel.DisplaySubject(email));
        Assert.Equal("c***@example.test", SmartStore.Pages.RecordsModel.MaskEmail(email.Recipient));
    }

    [MongoReplicaSetFact]
    public async Task ReceivingRejectsAnOverlengthEpcWithoutChangingStockOrHistory()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Products.ReceiveStockAsync(
            MongoTestDatabase.NewTaggedReception(product, 1, "00112233445566778899AABBCC"), db.Token));
        Assert.Equal(0, (await db.ReadProductAsync(product))!.QuantityInStock);
        Assert.Empty(await db.ReadItemsAsync(product));
        Assert.Empty(await db.ReadReceptionsAsync(product));
    }

    [MongoReplicaSetFact]
    public async Task ReportsCountStableAccountsIndependentlyOfSharedOrReassignedMailboxes()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var start = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        await db.InsertSalesAsync(
            new DemoSale { CreatedAtUtc = start.AddDays(-1), CustomerEmail = "shared@example.test", Total = 10m },
            new DemoSale { CreatedAtUtc = start.AddDays(-1), AccountId = MongoTestDatabase.MemberId, CustomerEmail = "shared@example.test", Total = 3m },
            new DemoSale { CreatedAtUtc = start, AccountId = MongoTestDatabase.MemberId, CustomerEmail = "shared@example.test", Total = 2m },
            new DemoSale { CreatedAtUtc = start, AccountId = "22222222222222222222222222222222", CustomerEmail = "shared@example.test", Total = 4m });
        var report = await db.Store.ReportAsync(start, start.AddDays(1), db.Token);
        Assert.Equal(6m, report.Revenue);
        Assert.Equal(2, report.Customers);
        Assert.Equal(1, report.ReturningCustomers);
        Assert.Equal(5m, await db.Store.CustomerTotalSpentAsync(MongoTestDatabase.MemberId, db.Token));
    }

    [MongoReplicaSetFact]
    public async Task LegacyMoneyRepresentationsRemainExactAndSnapshotsAreNeverRewritten()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid().ToString("N");
        var original = new MongoDB.Bson.BsonDocument
        {
            { "_id", id }, { "CreatedAtUtc", start }, { "PaymentStatus", "approved" },
            { "AccountId", MongoTestDatabase.MemberId }, { "Total", "12.34" },
            { "Items", new MongoDB.Bson.BsonArray { new MongoDB.Bson.BsonDocument
                { { "Code", "old-code" }, { "Name", "Historical name" }, { "Price", "12.34" }, { "Quantity", 1 } } } }
        };
        await db.InsertRawSaleAsync(original);
        await db.Store.BackfillCheckoutRecordsAsync(db.Token);
        var history = await db.Store.CustomerHistoryPageAsync(MongoTestDatabase.MemberId, 1, 10, "OLD-CODE", null, null, db.Token);
        Assert.Single(history.Sales);
        Assert.Equal(12.34m, history.Total);
        Assert.Equal(12.34m, await db.Store.CustomerTotalSpentAsync(MongoTestDatabase.MemberId, db.Token));
        Assert.Equal(12.34m, (await db.Store.CustomerSpendingAsync(MongoTestDatabase.MemberId, start, start.AddDays(1), db.Token)).Sum(b => b.Total));
        var report = await db.Store.ReportAsync(start, start.AddDays(1), db.Token);
        Assert.Equal(12.34m, report.Revenue);
        Assert.Equal("legacy:OLD-CODE", Assert.Single(report.Products).ProductKey);
        Assert.Equal(original, await db.ReadRawSaleAsync(id));
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
