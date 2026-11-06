-- Runs once, when the PostgreSQL container starts with an empty data volume.
-- Database per service: each service gets its own database AND its own login role.
-- identity_svc cannot even connect to core_db, so "never read another service's tables" is enforced
-- by the database, not just by convention.
-- These passwords are for local development only. Real deployments inject secrets.

CREATE ROLE identity_svc LOGIN PASSWORD 'dev_identity_pw';
CREATE ROLE network_svc  LOGIN PASSWORD 'dev_network_pw';
CREATE ROLE core_svc     LOGIN PASSWORD 'dev_core_pw';

CREATE DATABASE identity_db OWNER identity_svc;
CREATE DATABASE network_db  OWNER network_svc;
CREATE DATABASE core_db     OWNER core_svc;

-- By default every role may connect to every database. Take that away.
REVOKE CONNECT ON DATABASE identity_db FROM PUBLIC;
REVOKE CONNECT ON DATABASE network_db  FROM PUBLIC;
REVOKE CONNECT ON DATABASE core_db     FROM PUBLIC;
