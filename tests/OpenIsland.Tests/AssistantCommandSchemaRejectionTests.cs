using OpenIsland.App.Services.Commanding;

namespace OpenIsland.Tests;

public sealed class AssistantCommandSchemaRejectionTests
{
    public static TheoryData<string, string> WrongArgumentTypes => new()
    {
        { "create_todo", """{"title":42}""" },
        { "create_reminder", """{"title":"喝水","remind":"一小时后"}""" },
        { "create_event", """{"title":"开会","start":true,"end":false}""" },
        { "list_items", """{"range":[]}""" },
        { "update_todo", """{"target":"日报","changes":{}}""" },
        { "complete_todo", """{"target":1}""" },
        { "delete_todo", """{"target":true}""" },
        { "create_recurring_task", """{"title":"周报","wallStart":false,"recurrence":{}}""" },
        { "reschedule_item", """{"target":{},"newTime":5}""" },
        { "decompose_goal", """{"goal":"整理房间","constraints":"none"}""" },
        { "summarize_period", """{"period":"this_week"}""" }
    };

    [Theory]
    [MemberData(nameof(WrongArgumentTypes))]
    public void EveryArgumentsV1_RejectsWrongJsonTypes(string command, string arguments)
    {
        var error = Assert.Throws<AssistantCommandContractException>(() =>
            AssistantCommandEnvelopeJson.Deserialize(Envelope(command, arguments)));
        Assert.Equal("schema_rejected", error.Code);
    }

    public static TheoryData<string, string> CrossCommandInjection => new()
    {
        { "create_todo", """{"title":"买牛奶","confidence":0.99}""" },
        { "create_reminder", """{"title":"喝水","remind":null,"itemId":"x"}""" },
        { "create_event", """{"title":"开会","start":null,"end":null,"rowVersion":1}""" },
        { "list_items", """{"range":null,"confirmationId":"x"}""" },
        { "update_todo", """{"target":null,"changes":null,"todoId":"x"}""" },
        { "complete_todo", """{"target":null,"itemId":"x"}""" },
        { "delete_todo", """{"target":null,"clientRequestId":"x"}""" },
        { "create_recurring_task", """{"title":"周报","kind":"todo","wallStart":null,"recurrence":null,"id":"x"}""" },
        { "reschedule_item", """{"target":null,"newTime":null,"targetId":"x"}""" },
        { "decompose_goal", """{"goal":"整理房间","completed":true}""" },
        { "summarize_period", """{"period":null,"actionEventId":"x"}""" }
    };

    [Theory]
    [MemberData(nameof(CrossCommandInjection))]
    public void EveryArgumentsV1_RejectsUnknownOrTrustedLocalFields(string command, string arguments)
    {
        var error = Assert.Throws<AssistantCommandContractException>(() =>
            AssistantCommandEnvelopeJson.Deserialize(Envelope(command, arguments)));
        Assert.Equal("schema_rejected", error.Code);
    }

    [Fact]
    public void Contract_IsCaseSensitiveAndRejectsNumericEnums()
    {
        const string wrongCase = """{"SchemaVersion":1,"command":"create_todo","arguments":{"title":"买牛奶"},"missingFields":[],"ambiguityReasons":[]}""";
        var numericEnum = Envelope("create_todo", """{"title":"买牛奶","priority":2}""");

        Assert.Equal("schema_rejected",
            Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(wrongCase)).Code);
        Assert.Equal("schema_rejected",
            Assert.Throws<AssistantCommandContractException>(() => AssistantCommandEnvelopeJson.Deserialize(numericEnum)).Code);
    }

    [Fact]
    public void TimeExpression_MustPreserveOriginalUserText()
    {
        var json = Envelope("create_reminder",
            """{"title":"喝水","remind":{"relativeExpression":"一小时后"}}""");

        var error = Assert.Throws<AssistantCommandContractException>(() =>
            AssistantCommandEnvelopeJson.Deserialize(json));
        Assert.Equal("invalid_value", error.Code);
        Assert.Equal("$.arguments.remind.originalText", error.Path);
    }

    static string Envelope(string command, string arguments) =>
        $$"""{"schemaVersion":1,"command":"{{command}}","arguments":{{arguments}},"missingFields":[],"ambiguityReasons":[]}""";
}
