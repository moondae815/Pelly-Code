#!/usr/bin/env bash
# SQL 코퍼스를 SQL Server 컨테이너에 배포한다.
# 사용법: ./deploy.sh <컨테이너 이름 또는 ID> <sa 비밀번호> [DB 이름=PellyCorpus]
set -euo pipefail

container="$1"
password="$2"
db="${3:-PellyCorpus}"
here="$(cd "$(dirname "$0")" && pwd)"
sqlcmd=(/opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$password" -b)

docker exec "$container" rm -rf /tmp/corpus-sql
docker cp "$here/." "$container:/tmp/corpus-sql"

docker exec "$container" "${sqlcmd[@]}" -Q "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END; CREATE DATABASE [$db];"

for f in schema/01-tables.sql schema/02-seed.sql \
         objects/fn_OrderTotal.sql objects/vw_ShippedOrders.sql \
         objects/usp_CancelOrder.sql objects/usp_SearchOrders.sql objects/usp_GetOrderDetail.sql \
         objects/usp_SearchOrdersDynamic.sql objects/usp_BuildSettlement.sql objects/Settlement_All.sql; do
  echo "== $f"
  docker exec "$container" "${sqlcmd[@]}" -d "$db" -i "/tmp/corpus-sql/$f"
done

echo "== 배포 완료: $db"
