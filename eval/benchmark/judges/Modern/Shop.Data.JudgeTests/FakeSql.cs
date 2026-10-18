using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shop.Data.JudgeTests;

// 코퍼스 데이터 접근 코드(OrderRepository, DailySettlementJob)를 DB 없이 실행하기 위한 가짜.
// 진짜 패키지와 같은 네임스페이스·형식 이름이라 코퍼스 소스가 고치지 않고 이쪽에 묶인다.
// 수정본이 여기 없는 API를 쓰면 판정 프로젝트 빌드가 실패한다(판정 실패) — 자주 쓰는 API를 넓게 둔다.

namespace Microsoft.Data.SqlClient
{
    /// <summary>연결 문자열로 <see cref="FakeDatabase"/>를 찾아 명령을 넘기는 가짜 연결.</summary>
    public sealed class SqlConnection : IDbConnection, IAsyncDisposable
    {
        public SqlConnection(string connectionString) => ConnectionString = connectionString;

        public SqlConnection() { }

        public string ConnectionString { get; set; }
        public int ConnectionTimeout => 15;
        public string Database => "Fake";
        public ConnectionState State { get; private set; } = ConnectionState.Closed;

        internal FakeDatabase Db => FakeDatabase.For(ConnectionString);

        public void Open() => State = ConnectionState.Open;

        public Task OpenAsync() => OpenAsync(CancellationToken.None);

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            Open();
            return Task.CompletedTask;
        }

        public void Close() => State = ConnectionState.Closed;

        public Task CloseAsync()
        {
            Close();
            return Task.CompletedTask;
        }

        public SqlTransaction BeginTransaction() => BeginTransaction(IsolationLevel.ReadCommitted);

        public SqlTransaction BeginTransaction(IsolationLevel il) => new SqlTransaction(this, il);

        IDbTransaction IDbConnection.BeginTransaction() => BeginTransaction();

        IDbTransaction IDbConnection.BeginTransaction(IsolationLevel il) => BeginTransaction(il);

        public void ChangeDatabase(string databaseName) { }

        public IDbCommand CreateCommand() => throw new NotSupportedException("판정 가짜 연결은 Dapper 호출만 지원합니다.");

        public void Dispose() => Close();

        public ValueTask DisposeAsync()
        {
            Close();
            return default;
        }
    }

    /// <summary>커밋·롤백 횟수만 세는 가짜 트랜잭션.</summary>
    public sealed class SqlTransaction : IDbTransaction, IAsyncDisposable
    {
        private readonly SqlConnection _connection;

        internal SqlTransaction(SqlConnection connection, IsolationLevel isolationLevel)
        {
            _connection = connection;
            IsolationLevel = isolationLevel;
        }

        public IDbConnection Connection => _connection;
        public IsolationLevel IsolationLevel { get; }

        public void Commit() => _connection.Db.Commits++;

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Commit();
            return Task.CompletedTask;
        }

        public void Rollback() => _connection.Db.Rollbacks++;

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            Rollback();
            return Task.CompletedTask;
        }

        public void Dispose() { }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>코퍼스 코드가 catch할 수 있게만 둔다(가짜 DB는 던지지 않는다).</summary>
    public sealed class SqlException : Exception
    {
        public SqlException(string message) : base(message) { }
        public int Number => 0;
    }
}

namespace Dapper
{
    /// <summary>Dapper <c>DynamicParameters</c>와 같은 모양: 이름과 값만 기록한다.</summary>
    public sealed class DynamicParameters
    {
        internal Dictionary<string, object> Values { get; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public DynamicParameters() { }

        public DynamicParameters(object template) => AddDynamicParams(template);

        public void Add(string name, object value = null, DbType? dbType = null, ParameterDirection? direction = null, int? size = null,
            byte? precision = null, byte? scale = null) => Values[name.TrimStart('@')] = value;

        public void AddDynamicParams(object param)
        {
            foreach (var pair in FakeDatabase.ReadParameters(param)) Values[pair.Key] = pair.Value;
        }

        public T Get<T>(string name) => (T)FakeDatabase.Convert(Values[name.TrimStart('@')], typeof(T));

        public IEnumerable<string> ParameterNames => Values.Keys;
    }

    /// <summary>Dapper 확장 메서드와 같은 서명. 실제 실행 대신 <see cref="FakeDatabase"/>에 명령을 넘긴다.</summary>
    public static class SqlMapper
    {
        private static FakeDatabase Db(IDbConnection cnn) =>
            cnn is Microsoft.Data.SqlClient.SqlConnection c ? c.Db : throw new NotSupportedException("판정 가짜 연결이 아닙니다.");

        public static IEnumerable<T> Query<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            bool buffered = true, int? commandTimeout = null, CommandType? commandType = null) =>
            Db(cnn).Query<T>(sql, param, transaction);

        public static Task<IEnumerable<T>> QueryAsync<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) =>
            Task.FromResult(Db(cnn).Query<T>(sql, param, transaction));

        public static T QueryFirst<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Db(cnn).Query<T>(sql, param, transaction).First();

        public static T QueryFirstOrDefault<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Db(cnn).Query<T>(sql, param, transaction).FirstOrDefault();

        public static T QuerySingle<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Db(cnn).Query<T>(sql, param, transaction).Single();

        public static T QuerySingleOrDefault<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Db(cnn).Query<T>(sql, param, transaction).SingleOrDefault();

        public static Task<T> QueryFirstAsync<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Task.FromResult(cnn.QueryFirst<T>(sql, param, transaction));

        public static Task<T> QueryFirstOrDefaultAsync<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Task.FromResult(cnn.QueryFirstOrDefault<T>(sql, param, transaction));

        public static Task<T> QuerySingleAsync<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Task.FromResult(cnn.QuerySingle<T>(sql, param, transaction));

        public static Task<T> QuerySingleOrDefaultAsync<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Task.FromResult(cnn.QuerySingleOrDefault<T>(sql, param, transaction));

        public static int Execute(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Db(cnn).Execute(sql, param, transaction);

        public static Task<int> ExecuteAsync(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Task.FromResult(Db(cnn).Execute(sql, param, transaction));

        public static T ExecuteScalar<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) =>
            (T)FakeDatabase.Convert(Db(cnn).Scalar(sql, param, transaction), typeof(T));

        public static object ExecuteScalar(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Db(cnn).Scalar(sql, param, transaction);

        public static Task<T> ExecuteScalarAsync<T>(this IDbConnection cnn, string sql, object param = null, IDbTransaction transaction = null,
            int? commandTimeout = null, CommandType? commandType = null) => Task.FromResult(cnn.ExecuteScalar<T>(sql, param, transaction));
    }
}

namespace Shop.Data.JudgeTests
{
    /// <summary>가짜 DB에 넘어온 명령 하나: SQL 원문, 매개변수(이름은 <c>@</c> 없이, 대소문자 무시), 트랜잭션 안인지.</summary>
    public sealed class FakeCommand
    {
        public FakeCommand(string kind, string sql, IReadOnlyDictionary<string, object> parameters, bool inTransaction)
        {
            Kind = kind;
            Sql = sql;
            Parameters = parameters;
            InTransaction = inTransaction;
        }

        /// <summary><c>query</c>, <c>execute</c>, <c>scalar</c>.</summary>
        public string Kind { get; }
        public string Sql { get; }
        public IReadOnlyDictionary<string, object> Parameters { get; }
        public bool InTransaction { get; }

        public object this[string name] => Parameters.TryGetValue(name, out var v) ? v : throw new KeyNotFoundException($"매개변수 없음: {name} (SQL: {Sql})");
    }

    /// <summary>
    /// 테스트 하나가 쓰는 가짜 DB. <see cref="ConnectionString"/>으로 코퍼스 코드를 만들면 그 코드의 명령이 여기 기록된다.
    /// 조회 결과는 <see cref="OnQuery"/>가 열 이름→값 행으로 돌려주고, 결과 형식으로의 매핑은 Dapper처럼 이름(대소문자 무시)으로 한다.
    /// </summary>
    public sealed class FakeDatabase
    {
        private static readonly ConcurrentDictionary<string, FakeDatabase> Registry = new ConcurrentDictionary<string, FakeDatabase>();

        private FakeDatabase() => ConnectionString = "Server=judge-fake;Database=" + Guid.NewGuid().ToString("N");

        public static FakeDatabase Create()
        {
            var db = new FakeDatabase();
            Registry[db.ConnectionString] = db;
            return db;
        }

        internal static FakeDatabase For(string connectionString) =>
            connectionString != null && Registry.TryGetValue(connectionString, out var db)
                ? db
                : throw new InvalidOperationException("판정 가짜 DB에 없는 연결 문자열입니다.");

        public string ConnectionString { get; }
        public List<FakeCommand> Commands { get; } = new List<FakeCommand>();
        public Func<FakeCommand, IEnumerable<IDictionary<string, object>>> OnQuery { get; set; } = _ => Array.Empty<IDictionary<string, object>>();
        public Func<FakeCommand, object> OnScalar { get; set; } = _ => 0;
        public Func<FakeCommand, int> OnExecute { get; set; } = _ => 1;
        public int Commits { get; internal set; }
        public int Rollbacks { get; internal set; }

        internal IEnumerable<T> Query<T>(string sql, object param, IDbTransaction tx)
        {
            var command = Record("query", sql, param, tx);
            return OnQuery(command).Select(row => (T)Map(row, typeof(T))).ToList();
        }

        internal int Execute(string sql, object param, IDbTransaction tx) => OnExecute(Record("execute", sql, param, tx));

        internal object Scalar(string sql, object param, IDbTransaction tx) => OnScalar(Record("scalar", sql, param, tx));

        private FakeCommand Record(string kind, string sql, object param, IDbTransaction tx)
        {
            var command = new FakeCommand(kind, sql, ReadParameters(param), tx != null);
            Commands.Add(command);
            return command;
        }

        internal static Dictionary<string, object> ReadParameters(object param)
        {
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            switch (param)
            {
                case null:
                    break;
                case Dapper.DynamicParameters dynamic:
                    foreach (var pair in dynamic.Values) values[pair.Key] = pair.Value;
                    break;
                case IEnumerable<KeyValuePair<string, object>> pairs:
                    foreach (var pair in pairs) values[pair.Key.TrimStart('@')] = pair.Value;
                    break;
                default:
                    foreach (var p in param.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                        if (p.GetIndexParameters().Length == 0) values[p.Name] = p.GetValue(param);
                    break;
            }
            return values;
        }

        private static bool IsSimple(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(DateTime) || t == typeof(Guid);
        }

        private static object Map(IDictionary<string, object> row, Type type)
        {
            if (IsSimple(type)) return Convert(row.Values.FirstOrDefault(), type);
            var target = Activator.CreateInstance(type, nonPublic: true);
            foreach (var pair in row)
            {
                var property = type.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (property != null && property.CanWrite) property.SetValue(target, Convert(pair.Value, property.PropertyType));
            }
            return target;
        }

        internal static object Convert(object value, Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (value == null || value is DBNull)
                return type.IsValueType && underlying == null ? Activator.CreateInstance(type) : null;
            var t = underlying ?? type;
            if (t.IsInstanceOfType(value)) return value;
            if (t.IsEnum) return Enum.ToObject(t, System.Convert.ToInt32(value, CultureInfo.InvariantCulture));
            return System.Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>열 이름→값 행을 짧게 쓰기 위한 도우미.</summary>
    public static class Row
    {
        public static IDictionary<string, object> Of(params (string Column, object Value)[] columns) =>
            columns.ToDictionary(c => c.Column, c => c.Value, StringComparer.OrdinalIgnoreCase);
    }
}
