(module
  (func (export "spin")
    (loop
      br 0)
  )

  (func (export "countdown") (param $n i64) (result i64)
    (block $exit
      (loop $loop
        local.get $n
        i64.eqz
        br_if $exit
        local.get $n
        i64.const 1
        i64.sub
        local.set $n
        br $loop
      )
    )
    local.get $n
  )
)
