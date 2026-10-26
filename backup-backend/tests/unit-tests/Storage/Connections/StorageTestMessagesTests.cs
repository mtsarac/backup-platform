using BackupPlatform.Storage;
using Xunit;

namespace BackupPlatform.UnitTests.Storage.Connections;

public sealed class StorageTestMessagesTests
{
    [Fact]
    public void EveryStableTestCodeHasItsOwnSentenceInsteadOfTheUnexpectedFailureOne()
    {
        var codes = typeof(StorageTestCodes).GetFields().Where(field => field.IsLiteral).Select(field => (string)field.GetRawConstantValue()!)
            .Where(code => code != StorageTestCodes.Unexpected);

        Assert.All(codes, code => Assert.NotEqual(StorageTestMessages.For(StorageTestCodes.Unexpected), StorageTestMessages.For(code)));
    }
}
