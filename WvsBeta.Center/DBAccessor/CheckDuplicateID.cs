﻿using MySqlConnector;

namespace WvsBeta.Center.DBAccessor
{
    public partial class CharacterDBAccessor
    {
        public static bool CheckDuplicateID(string name)
        {
            using var reader = _characterDatabaseConnection.RunQuery(
                "SELECT 1 FROM characters WHERE name = @name AND world_id = @worldid AND deleted_at IS NULL",
                "@name", name,
                "@worldid", CenterServer.Instance.World.ID
            ) as MySqlDataReader;
            return reader.HasRows;
        }
    }
}
