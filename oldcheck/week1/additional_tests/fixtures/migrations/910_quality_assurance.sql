CREATE SCHEMA IF NOT EXISTS quality_assurance AUTHORIZATION course_owner;

CREATE TABLE IF NOT EXISTS quality_assurance.effects (
  effect_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  marker text NOT NULL,
  principal text NOT NULL,
  occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION quality_assurance.execute_v1(p_context jsonb, p_payload jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,quality_assurance AS $$
BEGIN
  INSERT INTO quality_assurance.effects(marker, principal)
  VALUES (p_payload->>'value', p_context->>'principal');

  IF p_payload->>'mode' = 'timeout' THEN
    PERFORM pg_sleep(1);
  ELSIF p_payload->>'mode' = 'error' THEN
    RETURN jsonb_build_object('status','error','code','assurance.rejected','message','rejected','retryable',false,'details','{}'::jsonb);
  ELSIF p_payload->>'mode' = 'unknown_outcome' THEN
    RETURN jsonb_build_object('status','ok','outcome','UNDECLARED','result',jsonb_build_object('value',p_payload->>'value'));
  ELSIF p_payload->>'mode' = 'invalid_result' THEN
    RETURN jsonb_build_object('status','ok','outcome','ACCEPTED','result',jsonb_build_object('value',42));
  END IF;

  RETURN jsonb_build_object(
    'status','ok',
    'outcome','ACCEPTED',
    'result',jsonb_build_object(
      'value',p_payload->>'value',
      'revision','v1',
      'principal',p_context->>'principal',
      'consumer',p_context->>'consumer',
      'scopes',p_context->'scopes',
      'requestId',p_context->>'requestId',
      'correlationId',p_context->>'correlationId',
      'deadline',p_context->>'deadline'));
END $$;

CREATE OR REPLACE FUNCTION quality_assurance.execute_v2(p_context jsonb, p_payload jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,quality_assurance AS $$
BEGIN
  INSERT INTO quality_assurance.effects(marker, principal)
  VALUES (p_payload->>'value', p_context->>'principal');
  RETURN jsonb_build_object('status','ok','outcome','ACCEPTED','result',
    jsonb_build_object('value',p_payload->>'value','revision','v2'));
END $$;

CREATE OR REPLACE FUNCTION quality_assurance.execute_scope(p_context jsonb, p_payload jsonb) RETURNS jsonb
LANGUAGE plpgsql SECURITY DEFINER SET search_path=pg_catalog,quality_assurance AS $$
BEGIN
  INSERT INTO quality_assurance.effects(marker, principal)
  VALUES (p_payload->>'value', p_context->>'principal');
  RETURN jsonb_build_object('status','ok','outcome','ACCEPTED','result',
    jsonb_build_object('value',p_payload->>'value','revision','scope'));
END $$;

ALTER FUNCTION quality_assurance.execute_v1(jsonb,jsonb) OWNER TO course_owner;
ALTER FUNCTION quality_assurance.execute_v2(jsonb,jsonb) OWNER TO course_owner;
ALTER FUNCTION quality_assurance.execute_scope(jsonb,jsonb) OWNER TO course_owner;
REVOKE ALL ON SCHEMA quality_assurance FROM PUBLIC,course_runtime;
REVOKE ALL ON ALL TABLES IN SCHEMA quality_assurance FROM PUBLIC,course_runtime;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA quality_assurance FROM PUBLIC,course_runtime;

CREATE OR REPLACE VIEW api.quality_assurance_evidence AS
SELECT marker, principal, occurred_at FROM quality_assurance.effects;
ALTER VIEW api.quality_assurance_evidence OWNER TO course_owner;
GRANT SELECT ON api.quality_assurance_evidence TO course_runtime;
