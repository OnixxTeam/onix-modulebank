-- Ticket 08: receipt.accept and Inbox. Inbox solves the mirror problem of
-- ticket 06's Outbox -- a receipt's arrival and its application to the
-- pinned process are different events, and the receipt can arrive before the
-- process ever reaches wait_receipt (SPEC "Квитанция может прийти раньше,
-- чем процесс дошёл до шага ожидания"). So this migration only ever
-- fixes the receipt in place; applying it to a process is ticket 09/10.
create schema receipt;

-- No grants to course_runtime here, same reasoning as core.external_requests
-- (migration 037): every read/write goes through accept_v1 below, owned by
-- course_owner.
create table core.receipts (
  message_id           text        primary key,
  external_request_id  text        not null references core.external_requests (external_request_id),
  message_version      integer     not null,
  outcome              text        not null check (outcome in ('COMPLETED', 'REJECTED')),
  signature_valid      boolean     not null,
  body_hash            text        not null,
  received_at          timestamptz not null default pg_catalog.now(),
  applied_at           timestamptz
);

-- No external_request_id-scoped uniqueness beyond the primary key: messageId
-- (== provider's own providerPaymentId) is the one and only dedup key
-- (SPEC "Идентификатор сообщения глобально уникален, а не уникален в
-- пределах внешнего запроса"), so a duplicate or conflicting callback of the
-- same provider payment lands on this same row regardless of which external
-- request or process it names.
create table delivery.inbox (
  message_id           text        primary key,
  external_request_id  text        not null references core.external_requests (external_request_id),
  body_hash            text        not null,
  -- CONFLICT is declared, not reached, by this ticket's own accept_v1: a
  -- conflicting body is rejected with 409 before any row is written or
  -- touched (SPEC "исходная запись ... не меняется"), never recorded as a
  -- state transition. Reserved for a future reconciliation outcome the same
  -- way workflow.workflow_job pre-declared DONE/FAILED (migration 022).
  state                text        not null check (state in ('RECEIVED', 'APPLIED', 'CONFLICT')) default 'RECEIVED',
  received_at          timestamptz not null default pg_catalog.now(),
  applied_at           timestamptz
);

-- receipt.accept version 1 target. The one business rule the boundary itself
-- does not decide (ticket 07's own note: "Требование «квитанция обязана быть
-- подписана» принадлежит самому действию") -- receipt.accept demands
-- transport.signatureVerified itself, as a controlled business error, not a
-- transport one.
--
-- Content-based dedup by messageId is deliberately separate from
-- api.invoke's own (scope_key, request_id) idempotency claim (migration
-- 007): that layer already replays an identical (Idempotency-Key, body)
-- pair without this function running again at all. What only this function
-- can see is the *same* messageId arriving under a *different*
-- Idempotency-Key -- DUPLICATE for a matching body, idempotency.conflict for
-- a mismatched one -- which is a domain-level rule the generic claim table
-- has no way to express (SPEC "Повтор с тем же messageId и тем же телом, но
-- другой командой идемпотентности, даёт исход дубля").
create function receipt.accept_v1(p_context jsonb, p_payload jsonb)
returns jsonb
language plpgsql
security definer
set search_path = ''
as $$
declare
  v_signature_verified  boolean;
  v_message_id          text := p_payload ->> 'messageId';
  v_external_request_id text := p_payload ->> 'externalRequestId';
  -- The exact SHA-256 of the exact HTTP body bytes, stamped by the signature
  -- boundary itself (ticket 07's context.rawBodyHash) -- never
  -- sha256(p_payload::text), which would hash Postgres's own jsonb-to-text
  -- rendering of the *parsed* payload instead of what actually crossed the
  -- wire. This is the same source of bytes the HMAC verified (SPEC "Хеш тела
  -- считается по тем же байтам, что и подпись"): two different sources here
  -- would only diverge on a byte-identical-content-but-differently-ordered
  -- resubmission, exactly the case a dedup-by-hash column exists to catch.
  v_body_hash           text := p_context ->> 'rawBodyHash';
  v_external_request    core.external_requests%rowtype;
  v_existing            delivery.inbox%rowtype;
begin
  v_signature_verified := coalesce((p_context -> 'transport' ->> 'signatureVerified')::boolean, false);

  if not v_signature_verified then
    return jsonb_build_object(
      'status', 'error',
      'code', 'receipt.signature_required',
      'message', 'receipt.accept requires a verified X-Provider-Signature'
    );
  end if;

  select * into v_external_request
    from core.external_requests
   where external_request_id = v_external_request_id;

  if not found then
    return jsonb_build_object(
      'status', 'error',
      'code', 'receipt.external_request_not_found',
      'message', 'unknown externalRequestId'
    );
  end if;

  select * into v_existing from delivery.inbox where message_id = v_message_id;

  if found then
    if v_existing.body_hash <> v_body_hash then
      return jsonb_build_object(
        'status', 'error',
        'code', 'idempotency.conflict',
        'message', 'messageId already recorded with a different body'
      );
    end if;

    return jsonb_build_object(
      'status', 'ok',
      'outcome', 'DUPLICATE',
      'result', jsonb_build_object(
        'messageId', v_existing.message_id,
        'externalRequestId', v_existing.external_request_id,
        'state', v_existing.state
      )
    );
  end if;

  insert into delivery.inbox (message_id, external_request_id, body_hash)
  values (v_message_id, v_external_request_id, v_body_hash);

  insert into core.receipts (message_id, external_request_id, message_version, outcome, signature_valid, body_hash)
  values (
    v_message_id, v_external_request_id, (p_payload ->> 'version')::integer,
    p_payload ->> 'outcome', v_signature_verified, v_body_hash
  );

  -- Forward only, never regressing an already-CONFIRMED delivery (SPEC
  -- "Подтверждение доставки не откатывает её в более раннее состояние") --
  -- the `<> 'CONFIRMED'` guard is what makes a second receipt for the same
  -- external request (should one ever arrive) a no-op here rather than a
  -- state change.
  update delivery.outbox
     set state = 'CONFIRMED', delivered_at = pg_catalog.now()
   where external_request_id = v_external_request_id
     and state <> 'CONFIRMED';

  update core.external_requests
     set state = 'CONFIRMED'
   where external_request_id = v_external_request_id
     and state <> 'CONFIRMED';

  return jsonb_build_object(
    'status', 'ok',
    'outcome', 'RECEIVED',
    'result', jsonb_build_object(
      'messageId', v_message_id,
      'externalRequestId', v_external_request_id,
      'state', 'RECEIVED'
    )
  );
end;
$$;

revoke all on function receipt.accept_v1(jsonb, jsonb) from public;

-- Stable observation boundary (migration 012's precedent). Neither view
-- exposes the signature, the secret, or the receipt's own full body -- only
-- the columns the contract names.
create view autocheck.receipts as
  select message_id, external_request_id, message_version, outcome, signature_valid, body_hash, received_at, applied_at
  from core.receipts;

grant select on autocheck.receipts to course_runtime;
revoke insert, update, delete on autocheck.receipts from course_runtime;

create view autocheck.inbox as
  select message_id, body_hash, state, received_at, applied_at
  from delivery.inbox;

grant select on autocheck.inbox to course_runtime;
revoke insert, update, delete on autocheck.inbox from course_runtime;
