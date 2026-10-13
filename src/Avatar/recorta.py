"""Separa una figura del fondo claro de la lámina: relleno desde los bordes que se detiene en el contorno dibujado."""
import sys
import numpy as np
import cv2
from scipy import ndimage as ndi


def recorta(img, caja, quitar=(), islas=(), tolerancia=9.0, nombre="figura"):
    """img: BGR completo. caja: (x0, y0, x1, y1). quitar: cajas (en coordenadas de la caja) donde se borra lo fino.
    islas: puntos (x, y) de huecos encerrados que también son fondo. Devuelve BGRA."""
    x0, y0, x1, y1 = caja
    rgb = img[y0:y1, x0:x1].astype(np.float32)
    h, w = rgb.shape[:2]

    # El fondo es un degradado suave: se estima con un desenfoque enorme de lo que seguro es fondo (lo muy claro y poco saturado).
    lum = rgb.mean(2)
    sat = rgb.max(2) - rgb.min(2)
    claro = (lum > 225) & (sat < 30)
    peso = cv2.GaussianBlur(claro.astype(np.float32), (0, 0), 40)
    fondo = cv2.GaussianBlur(rgb * claro[..., None], (0, 0), 40) / np.maximum(peso, 1e-3)[..., None]

    # Parecido al fondo: diferencia de color pequeña respecto al degradado.
    dif = np.abs(rgb - fondo).max(2)
    como_fondo = dif < tolerancia

    # Relleno desde el borde de la caja por lo que parece fondo.
    etiquetas, _ = ndi.label(como_fondo)
    borde = np.unique(np.concatenate([etiquetas[0], etiquetas[-1], etiquetas[:, 0], etiquetas[:, -1]]))
    borde = borde[borde != 0]
    es_fondo = np.isin(etiquetas, borde)
    for (ix, iy) in islas:
        if etiquetas[iy, ix]:
            es_fondo |= etiquetas == etiquetas[iy, ix]
        else:
            print(f"  aviso: la isla ({ix},{iy}) no cae en una zona de fondo", file=sys.stderr)

    figura = ~es_fondo
    # Lo fino que sobra (líneas del decorado que tocan la figura): se borra solo dentro de las cajas indicadas.
    for (qx0, qy0, qx1, qy1, radio) in quitar:
        zona = figura[qy0:qy1, qx0:qx1]
        nucleo = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (2 * radio + 1, 2 * radio + 1))
        figura[qy0:qy1, qx0:qx1] = cv2.morphologyEx(zona.astype(np.uint8), cv2.MORPH_OPEN, nucleo).astype(bool)

    # Pelusas de un píxel de ancho pegadas al contorno (restos de adornos del fondo).
    figura = cv2.morphologyEx(figura.astype(np.uint8), cv2.MORPH_OPEN, np.ones((3, 3), np.uint8)).astype(bool)
    # Queda solo la pieza más grande: motas y rótulos del fondo desaparecen.
    piezas, n = ndi.label(figura)
    if n > 1:
        areas = ndi.sum(figura, piezas, range(1, n + 1))
        figura = piezas == (1 + int(np.argmax(areas)))
    # Los huecos diminutos dentro de la figura (ruido del JPEG) se rellenan.
    huecos, n = ndi.label(~figura)
    if n > 1:
        areas = ndi.sum(~figura, huecos, range(1, n + 1))
        for i, a in enumerate(areas, 1):
            if a < 12:
                figura |= huecos == i

    # Borde suave: en la franja del contorno, la transparencia sale de cuánto se parece cada píxel al fondo o a la tinta.
    m = figura.astype(np.uint8)
    k3 = np.ones((3, 3), np.uint8)
    dentro = cv2.erode(m, k3, iterations=2).astype(bool)
    fuera = ~cv2.dilate(m, k3, iterations=2).astype(bool)
    franja = ~dentro & ~fuera
    # Tinta: el píxel más oscuro de los alrededores.
    gris = lum.astype(np.float32)
    minimo = cv2.erode(gris, np.ones((7, 7), np.uint8))
    tinta = np.zeros_like(rgb)
    # Para cada píxel de la franja, el color del vecino más oscuro (búsqueda en 7×7).
    ys, xs = np.nonzero(franja)
    relleno = cv2.copyMakeBorder(rgb, 3, 3, 3, 3, cv2.BORDER_REPLICATE)
    grel = cv2.copyMakeBorder(gris, 3, 3, 3, 3, cv2.BORDER_REPLICATE)
    for y, x in zip(ys, xs):
        v = grel[y:y + 7, x:x + 7]
        j = int(np.argmin(v))
        tinta[y, x] = relleno[y + j // 7, x + j % 7]
    num = ((rgb - fondo) * (tinta - fondo)).sum(2)
    den = ((tinta - fondo) ** 2).sum(2)
    alfa = np.where(dentro, 1.0, 0.0).astype(np.float32)
    con_tinta = franja & (den > 30 * 30)
    alfa[con_tinta] = np.clip(num[con_tinta] / den[con_tinta], 0, 1)
    sin_tinta = franja & ~con_tinta          # borde sin contorno oscuro: se queda con la máscara dura
    alfa[sin_tinta] = figura[sin_tinta]
    alfa[fuera] = 0
    alfa[alfa < 0.06] = 0

    color = rgb.copy()
    color[con_tinta] = tinta[con_tinta]      # en el borde, color de tinta pura: sin halo claro del fondo
    salida = np.dstack([np.clip(color, 0, 255), alfa * 255]).astype(np.uint8)
    salida[salida[..., 3] == 0] = 0          # lo transparente, sin restos de color: pesa menos y no ensucia al reducir
    print(f"{nombre}: {w}×{h}, figura {int(figura.sum())} px, franja {int(franja.sum())} px", file=sys.stderr)
    return salida


def sobre(bgra, color):
    a = bgra[..., 3:4].astype(np.float32) / 255
    base = np.empty_like(bgra[..., :3], dtype=np.float32)
    base[:] = color
    return (bgra[..., :3] * a + base * (1 - a)).astype(np.uint8)


def ajusta(bgra, margen=6):
    ys, xs = np.nonzero(bgra[..., 3] > 0)
    y0, y1, x0, x1 = max(0, ys.min() - margen), ys.max() + 1 + margen, max(0, xs.min() - margen), xs.max() + 1 + margen
    return bgra[y0:y1, x0:x1], (int(x0), int(y0))
