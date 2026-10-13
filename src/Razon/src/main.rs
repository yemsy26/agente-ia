// El motor lógico de Chispa. El resolutor es el de `dpll_check` (carpeta `motor/`, copiada sin cambios);
// este archivo solo le pone una entrada cómoda para un programa que le hace preguntas.
//
// Lee de la entrada estándar una base de cláusulas en DIMACS y, opcionalmente, varias consultas:
//
//   1 2 0            cláusula de la base (los literales pueden repartirse en varias líneas)
//   -1 -2 0
//   q 1 0            consulta: la base y además estos literales como ciertos
//   q -1 2 0
//
// Las líneas que empiezan por `c` o `p` se ignoran. Sin consultas resuelve la base una vez.
// Por cada consulta escribe una línea: `s SAT` seguida del modelo y un 0, o `s UNSAT`.
// Todo modelo sale comprobado contra la fórmula original por el propio motor.
#![forbid(unsafe_code)]

#[allow(dead_code)]
#[path = "motor/hybrid.rs"]
mod hybrid;

use hybrid::{solve_hybrid, HybridConfig};
use std::io::{self, Read, Write};

type Clause = Vec<i32>;

fn literals<'a>(tokens: impl Iterator<Item = &'a str>, into: &mut Clause) -> Result<bool, String> {
    for token in tokens {
        let literal: i32 = token.parse().map_err(|_| format!("Literal no válido: {token}"))?;
        if literal == i32::MIN {
            return Err(format!("Literal fuera de rango: {token}"));
        }
        if literal == 0 {
            return Ok(true);
        }
        into.push(literal);
    }
    Ok(false)
}

fn parse(text: &str) -> Result<(Vec<Clause>, Vec<Clause>), String> {
    let (mut base, mut queries, mut open) = (Vec::new(), Vec::new(), Clause::new());
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() || line.starts_with('c') || line.starts_with('p') {
            continue;
        }
        if let Some(rest) = line.strip_prefix('q') {
            let mut query = Clause::new();
            if !literals(rest.split_whitespace(), &mut query)? {
                return Err("Una consulta debe terminar en 0".into());
            }
            queries.push(query);
        } else if literals(line.split_whitespace(), &mut open)? {
            base.push(std::mem::take(&mut open));
        }
    }
    if open.is_empty() { Ok((base, queries)) } else { Err("La última cláusula no termina en 0".into()) }
}

fn run(text: &str) -> Result<String, String> {
    // Algunas consolas anteponen una marca de orden de bytes a lo que envían por la tubería.
    let (base, mut queries) = parse(text.trim_start_matches('\u{feff}'))?;
    if queries.is_empty() {
        queries.push(Clause::new());
    }
    let mut out = String::new();
    for query in queries {
        let mut formula = base.clone();
        formula.extend(query.iter().map(|&literal| vec![literal]));
        match solve_hybrid(&formula, HybridConfig::default()).model {
            Some(model) => {
                out.push_str("s SAT");
                for literal in model {
                    out.push_str(&format!(" {literal}"));
                }
                out.push_str(" 0\n");
            }
            None => out.push_str("s UNSAT\n"),
        }
    }
    Ok(out)
}

fn main() {
    let mut text = String::new();
    let outcome = io::stdin()
        .read_to_string(&mut text)
        .map_err(|error| format!("Entrada ilegible: {error}"))
        .and_then(|_| run(&text));
    match outcome {
        Ok(out) => {
            let _ = io::stdout().write_all(out.as_bytes());
        }
        Err(error) => {
            eprintln!("{error}");
            std::process::exit(2);
        }
    }
}
