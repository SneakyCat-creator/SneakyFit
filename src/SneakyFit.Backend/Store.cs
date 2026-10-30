using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SneakyFit.Backend;

public sealed class Store
{
    private readonly string _connectionString;
    public Store(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true, DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS users(id TEXT PRIMARY KEY, label TEXT NOT NULL, created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS invites(hash TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), expires_at INTEGER NOT NULL, used_at INTEGER);
            CREATE TABLE IF NOT EXISTS friend_invites(hash TEXT PRIMARY KEY, invited_by TEXT NOT NULL REFERENCES users(id), expires_at INTEGER NOT NULL, used_at INTEGER);
            CREATE TABLE IF NOT EXISTS invite_requests(user_id TEXT NOT NULL REFERENCES users(id), created_at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS invite_requests_created ON invite_requests(created_at);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, user_id TEXT NOT NULL REFERENCES users(id), name TEXT NOT NULL, token_hash TEXT NOT NULL UNIQUE, created_at INTEGER NOT NULL, revoked_at INTEGER);
            CREATE TABLE IF NOT EXISTS custom_products(id TEXT PRIMARY KEY, identity_key TEXT NOT NULL UNIQUE, search_text TEXT NOT NULL, body TEXT NOT NULL, created_by TEXT NOT NULL REFERENCES users(id), created_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS user_foods(user_id TEXT NOT NULL REFERENCES users(id), product_id TEXT NOT NULL, body TEXT NOT NULL, updated_at INTEGER NOT NULL, PRIMARY KEY(user_id,product_id));
            CREATE TABLE IF NOT EXISTS operations(user_id TEXT NOT NULL REFERENCES users(id), operation_key TEXT NOT NULL, request_hash TEXT NOT NULL, response TEXT NOT NULL, created_at INTEGER NOT NULL, PRIMARY KEY(user_id,operation_key));
            CREATE INDEX IF NOT EXISTS custom_products_created ON custom_products(created_at DESC);
            PRAGMA user_version=1;
            """;
        command.ExecuteNonQuery();
        using var columns = db.CreateCommand(); columns.CommandText = "PRAGMA table_info(custom_products)";
        bool hasSearchText; using (var reader = columns.ExecuteReader()) { hasSearchText = false; while (reader.Read()) hasSearchText |= reader.GetString(1) == "search_text"; }
        if (!hasSearchText)
        {
            using var tx = db.BeginTransaction(); using var alter = Command(db, "ALTER TABLE custom_products ADD COLUMN search_text TEXT NOT NULL DEFAULT ''", tx); alter.ExecuteNonQuery();
            using var select = Command(db, "SELECT body FROM custom_products", tx); var products = new List<CatalogProduct>();
            using (var reader = select.ExecuteReader()) while (reader.Read()) products.Add(JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0), JsonDefaults.Options)!);
            foreach (var product in products) { using var update = Command(db, "UPDATE custom_products SET search_text=$text WHERE id=$id", tx, ("$text", SearchText(product)), ("$id", product.Id)); update.ExecuteNonQuery(); }
            tx.Commit();
        }
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connectionString); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;"; command.ExecuteNonQuery(); return db;
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, SqliteTransaction? transaction = null, params (string, object?)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    public static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static string SearchText(CatalogProduct product) => string.Join(' ', new[] { FoodValidation.Normalize(product.Name), FoodValidation.Normalize(product.Brand ?? ""), FoodValidation.Normalize(product.Preparation ?? ""), product.Barcode ?? "" });
    public (string UserId, string Invite) CreateUser(string label)
    {
        label = FoodValidation.Text(label, "Имя", 100, true); var id = Guid.NewGuid().ToString("N");
        using var db = Open(); using var tx = db.BeginTransaction();
        using var command = Command(db, "INSERT INTO users(id,label,created_at) VALUES($id,$label,$now)", tx, ("$id", id), ("$label", label), ("$now", Now)); command.ExecuteNonQuery();
        var invite = InsertInvite(db, tx, id); tx.Commit(); return (id, invite);
    }
    public string CreateInvite(string userId) => CreateInvite(userId, false);
    public string CreateDeviceInvite(string userId) => CreateInvite(userId, true);
    public string CreateFriendInvite(string userId)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        RequireUser(db, tx, userId); RecordInviteRequest(db, tx, userId);
        var invite = Secret();
        using var command = Command(db, "INSERT INTO friend_invites(hash,invited_by,expires_at) VALUES($hash,$user,$expiry)", tx,
            ("$hash", Hash(invite)), ("$user", userId), ("$expiry", Now + 86400));
        command.ExecuteNonQuery(); tx.Commit(); return invite;
    }
    private string CreateInvite(string userId, bool limitDeviceRequests)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        RequireUser(db, tx, userId);
        if (limitDeviceRequests) RecordInviteRequest(db, tx, userId);
        var invite = InsertInvite(db, tx, userId); tx.Commit(); return invite;
    }
    private static void RequireUser(SqliteConnection db, SqliteTransaction tx, string userId)
    {
        using var exists = Command(db, "SELECT 1 FROM users WHERE id=$id", tx, ("$id", userId));
        if (exists.ExecuteScalar() is null) throw new ApiException(404, "Пользователь не найден.");
    }
    private static void RecordInviteRequest(SqliteConnection db, SqliteTransaction tx, string userId)
    {
        var now = Now;
        using var cleanup = Command(db, "DELETE FROM invite_requests WHERE created_at<=$cutoff", tx, ("$cutoff", now - 3600)); cleanup.ExecuteNonQuery();
        using var recent = Command(db, "SELECT COUNT(*),MIN(created_at) FROM invite_requests WHERE user_id=$user", tx, ("$user", userId));
        using (var reader = recent.ExecuteReader())
        {
            reader.Read();
            if (reader.GetInt64(0) >= 3) throw new ApiException(429, "Повторите создание кода позже.", (int)Math.Max(1, reader.GetInt64(1) + 3600 - now));
        }
        using var record = Command(db, "INSERT INTO invite_requests(user_id,created_at) VALUES($user,$now)", tx, ("$user", userId), ("$now", now)); record.ExecuteNonQuery();
    }
    private static string InsertInvite(SqliteConnection db, SqliteTransaction tx, string userId)
    {
        var invite = Secret(); using var command = Command(db, "INSERT INTO invites(hash,user_id,expires_at) VALUES($hash,$user,$expiry)", tx,
            ("$hash", Hash(invite)), ("$user", userId), ("$expiry", Now + 86400)); command.ExecuteNonQuery(); return invite;
    }
    public EnrollmentResult Enroll(EnrollmentRequest request)
    {
        var name = FoodValidation.Text(request.DeviceName, "Устройство", 80, true);
        if (request.Invite is null || request.Invite.Length != 43) throw new ApiException(401, "Приглашение недействительно или уже использовано.");
        using var db = Open(); using var tx = db.BeginTransaction(); var hash = Hash(request.Invite);
        using var query = Command(db, """
            SELECT user_id,0 FROM invites WHERE hash=$hash AND used_at IS NULL AND expires_at>$now
            UNION ALL
            SELECT invited_by,1 FROM friend_invites WHERE hash=$hash AND used_at IS NULL AND expires_at>$now
            """, tx, ("$hash", hash), ("$now", Now));
        string userId; bool isFriend;
        using (var reader = query.ExecuteReader())
        {
            if (!reader.Read()) throw new ApiException(401, "Приглашение недействительно или уже использовано.");
            userId = reader.GetString(0); isFriend = reader.GetInt64(1) == 1;
        }
        var consumeSql = isFriend ? "UPDATE friend_invites SET used_at=$now WHERE hash=$hash AND used_at IS NULL"
            : "UPDATE invites SET used_at=$now WHERE hash=$hash AND used_at IS NULL";
        using var consume = Command(db, consumeSql, tx, ("$now", Now), ("$hash", hash));
        if (consume.ExecuteNonQuery() != 1) throw new ApiException(401, "Приглашение уже использовано.");
        if (isFriend)
        {
            userId = Guid.NewGuid().ToString("N");
            using var user = Command(db, "INSERT INTO users(id,label,created_at) VALUES($id,'SneakyFit',$now)", tx, ("$id", userId), ("$now", Now));
            user.ExecuteNonQuery();
        }
        var token = Secret();
        using var device = Command(db, "INSERT INTO devices(id,user_id,name,token_hash,created_at) VALUES($id,$user,$name,$hash,$now)", tx,
            ("$id", Guid.NewGuid().ToString("N")), ("$user", userId), ("$name", name), ("$hash", Hash(token)), ("$now", Now)); device.ExecuteNonQuery();
        tx.Commit(); return new(token, userId);
    }
    public Identity? Authenticate(string token)
    {
        if (token.Length != 43 || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        using var db = Open(); using var command = Command(db, "SELECT user_id,id FROM devices WHERE token_hash=$hash AND revoked_at IS NULL", null, ("$hash", Hash(token)));
        using var reader = command.ExecuteReader(); return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) : null;
    }
    public bool RevokeDevice(string id)
    {
        using var db = Open(); using var command = Command(db, "UPDATE devices SET revoked_at=$now WHERE id=$id AND revoked_at IS NULL", null, ("$now", Now), ("$id", id)); return command.ExecuteNonQuery() == 1;
    }
    public object[] ListDevices(string userId)
    {
        using var db = Open(); using var command = Command(db, "SELECT id,name,created_at,revoked_at FROM devices WHERE user_id=$user ORDER BY created_at", null, ("$user", userId)); using var reader = command.ExecuteReader();
        var items = new List<object>(); while (reader.Read()) items.Add(new { id = reader.GetString(0), name = reader.GetString(1), createdAt = reader.GetInt64(2), revoked = !reader.IsDBNull(3) }); return items.ToArray();
    }
    public CatalogProduct? GetCustom(string id)
    {
        using var db = Open(); using var command = Command(db, "SELECT body FROM custom_products WHERE id=$id", null, ("$id", id));
        return command.ExecuteScalar() is string body ? JsonSerializer.Deserialize<CatalogProduct>(body, JsonDefaults.Options) : null;
    }
    public CatalogProduct? CustomByBarcode(string barcode)
    {
        using var db = Open(); using var command = Command(db, "SELECT body FROM custom_products WHERE identity_key=$key", null, ("$key", "barcode:" + barcode));
        return command.ExecuteScalar() is string body ? JsonSerializer.Deserialize<CatalogProduct>(body, JsonDefaults.Options) : null;
    }
    public IReadOnlyList<CatalogProduct> SearchCustom(string query, int page, int size = 20)
    {
        // A normalized identity substring is enough for the small shared family catalog;
        // OFF stays in a separately licensed/cache-only database.
        var term = FoodValidation.Normalize(query).Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
        using var db = Open(); using var command = Command(db,
            "SELECT body FROM custom_products WHERE search_text LIKE $term ESCAPE '!' ORDER BY created_at DESC,id LIMIT $limit OFFSET $offset", null,
            ("$term", "%" + term + "%"), ("$limit", size + 1), ("$offset", (page - 1) * size));
        using var reader = command.ExecuteReader(); var result = new List<CatalogProduct>(); while (reader.Read()) result.Add(JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0), JsonDefaults.Options)!); return result;
    }
    public CustomFoodResult CreateCustom(string userId, CatalogProduct requested, string? operationKey = null, CatalogProduct? externalCanonical = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction(); var key = FoodValidation.IdentityKey(requested);
        var requestHash = Hash(JsonSerializer.Serialize(requested, JsonDefaults.Options));
        if (operationKey is not null)
        {
            using var replay = Command(db, "SELECT request_hash,response FROM operations WHERE user_id=$user AND operation_key=$key", tx, ("$user", userId), ("$key", operationKey));
            using var reader = replay.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != requestHash) throw new ApiException(409, "Этот ключ операции уже использован для другого запроса.");
                return JsonSerializer.Deserialize<CustomFoodResult>(reader.GetString(1), JsonDefaults.Options)!;
            }
        }
        using var existing = Command(db, "SELECT body FROM custom_products WHERE identity_key=$key", tx, ("$key", key)); var found = existing.ExecuteScalar() as string;
        var canonical = externalCanonical ?? (found is null ? requested with { Id = "custom:" + Guid.NewGuid().ToString("N"), Revision = Guid.NewGuid().ToString("N") }
            : JsonSerializer.Deserialize<CatalogProduct>(found, JsonDefaults.Options)!);
        if (found is null && externalCanonical is null)
        {
            using var insert = Command(db, "INSERT INTO custom_products(id,identity_key,search_text,body,created_by,created_at) VALUES($id,$key,$search,$body,$user,$now)", tx,
                ("$id", canonical.Id), ("$key", key), ("$search", SearchText(canonical)), ("$body", JsonSerializer.Serialize(canonical, JsonDefaults.Options)), ("$user", userId), ("$now", Now)); insert.ExecuteNonQuery();
        }
        var personal = SavePersonal(db, tx, userId, requested with { Id = canonical.Id, Barcode = canonical.Barcode, Source = canonical.Source, SourceUrl = canonical.SourceUrl });
        var result = new CustomFoodResult(canonical, personal, found is not null || externalCanonical is not null);
        if (operationKey is not null)
        {
            using var remember = Command(db, "INSERT INTO operations(user_id,operation_key,request_hash,response,created_at) VALUES($user,$key,$hash,$response,$now)", tx,
                ("$user", userId), ("$key", operationKey), ("$hash", requestHash), ("$response", JsonSerializer.Serialize(result, JsonDefaults.Options)), ("$now", Now)); remember.ExecuteNonQuery();
        }
        tx.Commit(); return result;
    }
    public CatalogProduct SavePersonal(string userId, CatalogProduct product, string? expectedRevision = null)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        using var current = Command(db, "SELECT json_extract(body,'$.revision') FROM user_foods WHERE user_id=$user AND product_id=$product", tx, ("$user", userId), ("$product", product.Id));
        var currentRevision = current.ExecuteScalar() as string;
        if (currentRevision != expectedRevision) throw new ApiException(409, "Продукт уже изменён на другом устройстве. Обновите личный список.");
        var result = SavePersonal(db, tx, userId, product); tx.Commit(); return result;
    }
    private static CatalogProduct SavePersonal(SqliteConnection db, SqliteTransaction tx, string userId, CatalogProduct product)
    {
        var result = product with { Revision = Guid.NewGuid().ToString("N") };
        using var command = Command(db, "INSERT INTO user_foods(user_id,product_id,body,updated_at) VALUES($user,$product,$body,$now) ON CONFLICT(user_id,product_id) DO UPDATE SET body=excluded.body,updated_at=excluded.updated_at", tx,
            ("$user", userId), ("$product", result.Id), ("$body", JsonSerializer.Serialize(result, JsonDefaults.Options)), ("$now", Now)); command.ExecuteNonQuery(); return result;
    }
    public IReadOnlyList<CatalogProduct> Personal(string userId)
    {
        using var db = Open(); using var command = Command(db, "SELECT body FROM user_foods WHERE user_id=$user ORDER BY updated_at DESC,product_id", null, ("$user", userId));
        using var reader = command.ExecuteReader(); var result = new List<CatalogProduct>(); while (reader.Read()) result.Add(JsonSerializer.Deserialize<CatalogProduct>(reader.GetString(0), JsonDefaults.Options)!); return result;
    }
    public void Backup(string target)
    {
        using var source = Open(); using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target }.ToString()); destination.Open(); source.BackupDatabase(destination);
    }
}
