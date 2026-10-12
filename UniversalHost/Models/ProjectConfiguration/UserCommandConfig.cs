using DynamicData;
using ReactiveUI.Reactive;
using ReactiveUI.SourceGenerators;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System;

namespace UniversalHost.Models;

public partial class UserCommand : ReactiveObject
{
    public byte Id { get; init; } = 0;
    public string IdHex => Id.ToString("X2");
    [Reactive] private string _name = string.Empty;
    [Reactive] private string _description = string.Empty;
    public static Func<UserCommand, bool> CreateUserSymbolInfoSearchFilter(string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return _ => true; // 显示所有项
        }
        var lowerSearchText = searchText.ToLowerInvariant();
        return symbol =>
            symbol.IdHex.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.Name?.ToLowerInvariant().Contains(lowerSearchText) == true ||
            symbol.Description?.ToLowerInvariant().Contains(lowerSearchText) == true;
    }
}

public partial class UserCommandConfig : ReactiveObject
{
    [JsonIgnore] public SourceCache<UserCommand, byte> UserCommands { get; } = new SourceCache<UserCommand, byte>(x => x.Id);
    [JsonPropertyName("UserCommands")] // 确保 JSON 中的名称保持不变
    public List<UserCommand> UserCommandsStorage
    {
        get => UserCommands.Items.ToList(); // 保存时
        set
        {
            if (value == null)
                value = new List<UserCommand>();

            var dict = value.ToDictionary(x => x.Id);

            UserCommands.Edit(updater =>
            {
                updater.Clear();
                for (int i = 0; i <= byte.MaxValue; i++)
                {
                    dict.TryGetValue((byte)i, out var item);

                    var cmd = new UserCommand
                    {
                        Id = (byte)i,
                        Name = item?.Name ?? string.Empty,
                        Description = item?.Description ?? string.Empty
                    };

                    updater.AddOrUpdate(cmd);
                }
            });
        }
    }
    public UserCommandConfig()
    {
        for (int i = 0; i <= byte.MaxValue; i++)
        {
            var cmd = new UserCommand()
            {
                Id = (byte)i,
            };
            UserCommands.AddOrUpdate(cmd);
        }
    }
}
