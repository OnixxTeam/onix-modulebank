create schema api;
create schema core;
create schema payment;
create schema operation;
create schema autocheck;
create schema migration;

create table migration.applied_files (
  file_name  text        primary key,
  checksum   text        not null,
  applied_at timestamptz not null default now()
);
