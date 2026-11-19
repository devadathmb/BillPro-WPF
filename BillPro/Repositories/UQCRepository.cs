using System.Collections.Generic;
using System.Data;
using Dapper;
using BillPro.DataAccess;
using BillPro.Models;

namespace BillPro.Repositories
{
    // 1. Inherit from GenericRepository
    public class UqcRepository : GenericRepository<UQCItem>
    {
        // 2. Pass the exact database table name
        public UqcRepository() : base("uqc_master")
        {
        }

        public void InitializeAndSeed()
        {
            using (IDbConnection db = DatabaseConfig.GetConnection())
            {
                bool tableNeedsRebuilding = false;
                try
                {
                    db.ExecuteScalar("SELECT description FROM uqc_master LIMIT 1");
                }
                catch
                {
                    tableNeedsRebuilding = true;
                }

                if (tableNeedsRebuilding)
                {
                    db.Execute("DROP TABLE IF EXISTS uqc_master;");
                }

                db.Execute("CREATE TABLE IF NOT EXISTS uqc_master (uqc TEXT PRIMARY KEY, description TEXT);");

                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM uqc_master") == 0)
                {
                    var uqcs = new[]
                    {
                        new { UQC = "BAG", Description = "BAGS" }, new { UQC = "BAL", Description = "BALE" },
                        new { UQC = "BDL", Description = "BUNDLES" }, new { UQC = "BKL", Description = "BUCKLES" },
                        new { UQC = "BOU", Description = "BILLIONS OF UNITS" }, new { UQC = "BOX", Description = "BOX" },
                        new { UQC = "BTL", Description = "BOTTLES" }, new { UQC = "BUN", Description = "BUNCHES" },
                        new { UQC = "CAN", Description = "CANS" }, new { UQC = "CBM", Description = "CUBIC METER" },
                        new { UQC = "CCM", Description = "CUBIC CENTIMETER" }, new { UQC = "CMS", Description = "CENTIMETER" },
                        new { UQC = "CTN", Description = "CARTONS" }, new { UQC = "DOZ", Description = "DOZEN" },
                        new { UQC = "DRM", Description = "DRUM" }, new { UQC = "GGR", Description = "GREAT GROSS" },
                        new { UQC = "GMS", Description = "GRAMS" }, new { UQC = "GRS", Description = "GROSS" },
                        new { UQC = "GYD", Description = "GROSS YARDS" }, new { UQC = "KGS", Description = "KILOGRAMS" },
                        new { UQC = "KLR", Description = "KILOLITRE" }, new { UQC = "KME", Description = "KILOMETRE" },
                        new { UQC = "MLT", Description = "MILLILITRE" }, new { UQC = "MTR", Description = "METERS" },
                        new { UQC = "MTS", Description = "METRIC TON" }, new { UQC = "NOS", Description = "NUMBERS" },
                        new { UQC = "PAC", Description = "PACKS" }, new { UQC = "PCS", Description = "PIECES" },
                        new { UQC = "PRS", Description = "PAIRS" }, new { UQC = "QTL", Description = "QUINTAL" },
                        new { UQC = "ROL", Description = "ROLLS" }, new { UQC = "SET", Description = "SETS" },
                        new { UQC = "SQF", Description = "SQUARE FEET" }, new { UQC = "SQM", Description = "SQUARE METERS" },
                        new { UQC = "SQY", Description = "SQUARE YARDS" }, new { UQC = "TBS", Description = "TABLETS" },
                        new { UQC = "TGM", Description = "TEN GRAMS" }, new { UQC = "THD", Description = "THOUSANDS" },
                        new { UQC = "TON", Description = "TONNES" }, new { UQC = "TUB", Description = "TUBES" },
                        new { UQC = "UGS", Description = "US GALLONS" }, new { UQC = "UNT", Description = "UNITS" },
                        new { UQC = "YDS", Description = "YARDS" }, new { UQC = "OTH", Description = "OTHERS" }
                    };

                    db.Execute("INSERT INTO uqc_master (uqc, description) VALUES (@UQC, @Description)", uqcs);
                }
            }
        }
    }
}