"""Del dibujo original a las piezas que usa Chispa: recorte, párpados, bocas, antebrazo, mapa del pelo y medidas.

    python monta.py original.jpg carpeta_de_salida
"""
import json
import os
import sys
import numpy as np
import cv2
from scipy import ndimage as ndi

import recorta
import expresiones
import brazo


def mapa_del_pelo(base):
    """Cuánto se mece cada punto: 0 nada … 255 del todo. Solo el pelo largo que cae a los lados, más cuanto más abajo."""
    h, w = base.shape[:2]
    bgr = base[..., :3].astype(np.float32)
    b, r = bgr[..., 0], bgr[..., 2]
    lavanda = (b - r > 30) & (r > 120) & (b > 200) & (base[..., 3] > 0)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    lavanda &= (yy > 150) & (np.abs(xx - 278) > 72) & (yy < 470)
    lavanda = cv2.morphologyEx(lavanda.astype(np.uint8), cv2.MORPH_CLOSE, np.ones((7, 7), np.uint8))
    piezas, n = ndi.label(lavanda)
    if n:
        areas = ndi.sum(lavanda, piezas, range(1, n + 1))
        lavanda = np.isin(piezas, [i + 1 for i, a in enumerate(areas) if a > 400]).astype(np.uint8)
    suave = cv2.GaussianBlur(lavanda.astype(np.float32), (0, 0), 6)
    rampa = np.clip((yy - 175) / 250, 0, 1) ** 1.3
    mapa = np.clip(suave * 1.15, 0, 1) * rampa
    return cv2.resize((mapa * 255).astype(np.uint8), (w // 2, h // 2), interpolation=cv2.INTER_AREA)


def monta(original, salida):
    os.makedirs(salida, exist_ok=True)
    img = cv2.imread(original)
    figura = recorta.recorta(img, (690, 30, 1310, 1116), islas=[(310, 800)],
                             quitar=[(0, 955, 233, 1005, 2), (284, 955, 333, 1005, 2), (389, 955, 620, 1005, 2)], nombre="frente")
    figura, _ = recorta.ajusta(figura, 8)
    cv2.imwrite(f"{salida}/cuerpo.png", figura)
    h, w = figura.shape[:2]

    partes = expresiones.genera(figura, salida)
    capa, origen, sin_brazo = brazo.capas(figura)
    # Del cuerpo sin antebrazo solo queda la pieza grande (fuera restos sueltos).
    piezas, n = ndi.label(sin_brazo[..., 3] > 0)
    if n > 1:
        areas = ndi.sum(sin_brazo[..., 3] > 0, piezas, range(1, n + 1))
        sin_brazo[piezas != 1 + int(np.argmax(areas))] = 0
    cv2.imwrite(f"{salida}/brazo.png", capa)
    cv2.imwrite(f"{salida}/sin_brazo.png", sin_brazo)
    cv2.imwrite(f"{salida}/pelo.png", mapa_del_pelo(figura))

    filas = np.nonzero((figura[..., 3] > 128).any(1))[0]
    suela = figura[filas[-1] - 12:filas[-1] + 1, :, 3] > 128
    columnas = np.nonzero(suela.any(0))[0]
    medidas = {
        "ancho": w, "alto": h,
        "coronilla": int(filas[0]), "suelo": int(filas[-1]) + 1,
        "centro": round(float(columnas[0] + columnas[-1]) / 2, 1),
        # cabeza
        "cuello": [278.0, 197.0],          # punto sobre el que se inclina la cabeza
        "menton": 178.0, "hombros": 234.0, # por encima del mentón todo es cabeza; por debajo de los hombros, nada
        "cara": [278.0, 127.0, 78.0, 96.0],  # centro y radios: lo que se desplaza al mirar a los lados
        "boca": [278.3, 161.0],
        "orejas": [[206.0, 121.0], [351.0, 121.0]],   # los auriculares: ahí se encienden las luces de «te escucho»
        # cuerpo
        "cintura": 402.0,                  # de los hombros a la cintura sube y baja al respirar
        "falda": [406.0, 566.0, 604.0, 128.0, 166.0],   # empieza, vuelo, fin del fundido, y medio ancho (lleno / nada)
        "cadera": 580.0, "piernas": 278.5, # las piernas se levantan desde la cadera; x que separa una de otra
        "pelo": {"escala": 2},
        "brazo": {"x": origen[0], "y": origen[1], "codo": list(brazo.CODO), "r0": brazo.R0, "r1": brazo.R1, "sube": 128.0},
        "partes": partes,
    }
    json.dump(medidas, open(f"{salida}/figura.json", "w", encoding="utf-8"), indent=1)
    return medidas


if __name__ == "__main__":
    m = monta(sys.argv[1], sys.argv[2])
    print({k: m[k] for k in ("ancho", "alto", "coronilla", "suelo", "centro")})
