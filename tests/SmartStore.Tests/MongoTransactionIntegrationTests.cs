using MongoDB.Driver;
using SmartStore.Data;
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
    public Task ConcurrentDuplicateEpcAllowsOnlyOneProduct() => AssertConcurrentDuplicateAsync("EPC");

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
    public async Task GuestEmailReceivesReceiptWithoutALoyaltyAccount()
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var product = await db.AddProductAsync(stock: 2, price: 2.50m);
        var cart = new Dictionary<string, int> { [product.Upc] = 1 };
        var sale = await db.Store.CheckoutAsync(cart, "approve", " Guest@Example.test ", db.Token);
        var stored = Assert.Single(await db.ReadSalesAsync());
        Assert.Equal(sale.Id, stored.Id);
        Assert.Equal("guest@example.test", stored.CustomerEmail);
        Assert.Equal("pending", stored.EmailStatus);
        Assert.Null(stored.CustomerId);
        Assert.Equal(0, stored.PointsEarned);
        var declined = await db.Store.CheckoutAsync(cart, "decline", "guest@example.test", db.Token);
        Assert.Equal("not_requested", declined.EmailStatus);
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
        var product = await db.AddProductAsync(stock: 10, price: 3.25m);

        var sale = await db.Store.CheckoutAsync(new Dictionary<string, int>
        {
            [product.Upc] = 2,
            [product.Epc.ToLowerInvariant()] = 3
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

    private static async Task AssertConcurrentDuplicateAsync(string field)
    {
        await using var db = await MongoTestDatabase.CreateAsync();
        var first = MongoTestDatabase.NewProduct(ordinal: 1);
        var second = MongoTestDatabase.NewProduct(ordinal: 2);
        if (field == "UPC") second.Upc = first.Upc;
        else second.Epc = first.Epc;

        var failures = await RunTogetherAsync(
            () => Record.ExceptionAsync(() => db.Products.AddAsync(first, db.Token)),
            () => Record.ExceptionAsync(() => db.Products.AddAsync(second, db.Token)));

        Assert.Single(failures, failure => failure is null);
        var failure = Assert.Single(failures, error => error is not null);
        Assert.Equal(field, Assert.IsType<DuplicateProductCodeException>(failure).Field);
        var stored = Assert.Single(await db.ReadProductsAsync());
        Assert.Equal(field == "UPC" ? first.Upc : first.Epc, field == "UPC" ? stored.Upc : stored.Epc);
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
