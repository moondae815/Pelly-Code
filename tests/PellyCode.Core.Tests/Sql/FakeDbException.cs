using System.Data.Common;

namespace PellyCode.Core.Tests.Sql;

/// <summary>Stands in for a SqlException that is not connection-level (e.g. Msg 208): SqlException has no public constructor.</summary>
internal sealed class FakeDbException(string message) : DbException(message);
