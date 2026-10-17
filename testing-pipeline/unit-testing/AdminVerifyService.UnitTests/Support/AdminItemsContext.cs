using System.Security.Claims;
using ItemService.Configuration;
using ItemService.Controllers;
using ItemService.Models;
using ItemService.Repositories;
using ItemService.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AdminVerifyService.UnitTests.Support;

internal sealed class AdminItemsContext
{
    public Mock<IAdminItemsRepository> Items { get; } = new(MockBehavior.Strict);
    public Mock<IEventPublisher> Events { get; } = new(MockBehavior.Strict);
    public Mock<ILogger<AdminItemsController>> Log { get; } = new();
    public Guid AdminId { get; } = Guid.NewGuid();
    public AdminItemsController Controller { get; }

    public AdminItemsContext()
    {
        Controller = new AdminItemsController(Items.Object, Events.Object,
            Options.Create(new KafkaSettings { TopicPrefix = "items" }), Log.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", AdminId.ToString())], "test"));
    }

    public static AdminItemRecord Record(AdminItemType type, string status = "ACTIVE", bool deleted = false) => new()
    {
        Id = Guid.NewGuid(), Type = type, UserId = Guid.NewGuid(), Title = "Blue bag",
        Description = "Canvas bag with front pocket", Status = status,
        CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        DeletedAt = deleted ? new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc) : null
    };
}
