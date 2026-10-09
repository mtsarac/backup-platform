#!/usr/bin/env python3
"""Create/verify only M5-owned databases in the disposable SQL container."""
import argparse
import json
import os
import re
import subprocess
import sys


def run_sql(container, sql):
    if not re.fullmatch(r'bp-local-sql-bp-local\.[A-Za-z0-9]+', container):
        raise ValueError('Only with-disposable-databases.sh SQL containers are permitted.')
    environment = dict(os.environ)
    environment['SQLCMDPASSWORD'] = environment['BACKUP_PLATFORM_TEST_MSSQL_PASSWORD']
    result = subprocess.run(['docker', 'exec', '-i', '-e', 'SQLCMDPASSWORD', container,
                             '/opt/mssql-tools18/bin/sqlcmd', '-S', 'localhost', '-U',
                             environment['BACKUP_PLATFORM_TEST_MSSQL_USER'], '-C', '-b', '-h', '-1', '-W'],
                            input=sql, text=True, capture_output=True, env=environment, timeout=45)
    if result.returncode:
        raise ValueError('Disposable SQL command failed; provider diagnostics withheld.')
    return result.stdout.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command', choices=('setup', 'verify'))
    parser.add_argument('--container', required=True)
    parser.add_argument('--database', required=True)
    options = parser.parse_args()
    if not re.fullmatch(r'bp_m5_(source|target)_[0-9a-f]{12}(_restored)?', options.database):
        raise ValueError('Only generated M5 smoke database names are permitted.')
    database = options.database
    if options.command == 'setup':
        if not database.startswith('bp_m5_source_'):
            raise ValueError('Setup may create only the source database.')
        run_sql(options.container, f"SET NOCOUNT ON; CREATE DATABASE [{database}];\nGO\nUSE [{database}]; CREATE TABLE dbo.RestoreSmoke (Id int PRIMARY KEY, Marker nvarchar(100) NOT NULL); INSERT dbo.RestoreSmoke VALUES (1, N'm5_browser_restore'), (2, N'expected_second_row');\nGO\n")
        print(json.dumps({'sourceDatabase': database, 'expectedRows': 2}))
    else:
        output = run_sql(options.container, f"SET NOCOUNT ON; SELECT state_desc FROM sys.databases WHERE name=N'{database}'; SELECT COUNT(*) FROM [{database}].dbo.RestoreSmoke; SELECT Marker FROM [{database}].dbo.RestoreSmoke WHERE Id=1;\nGO\n")
        values = [line.strip() for line in output.splitlines() if line.strip()]
        if values != ['ONLINE', '2', 'm5_browser_restore']:
            raise ValueError('Restored database state or expected rows differ.')
        print(json.dumps({'targetDatabase': database, 'state': 'ONLINE', 'expectedRowsVerified': True, 'markerVerified': True}))


if __name__ == '__main__':
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as failure:
        print('FAIL: ' + (str(failure) if isinstance(failure, ValueError) else 'Disposable SQL verification failed.'), file=sys.stderr)
        sys.exit(1)
