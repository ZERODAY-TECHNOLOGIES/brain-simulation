# 3D model attribution

`brain.bin` is derived from **BodyParts3D**, (c) The Database Center for Life Science,
licensed under [Creative Commons Attribution-Share Alike 2.1 Japan](https://creativecommons.org/licenses/by-sa/2.1/jp/deed.en).

> Mitsuhashi N, Fujieda K, Tamura T, Kawamoto S, Takagi T, Okubo K.
> BodyParts3D: 3D structure database for anatomical concepts.
> Nucleic Acids Res. 2009 Jan;37(Database issue):D782-5. https://doi.org/10.1093/nar/gkn613

The STL conversion used as the source is the mirror at
https://github.com/Kevin-Mattheus-Moerman/BodyParts3D (release 3.0, 20110915).

`tools/MeshPrep` builds `brain.bin`: it merges the parts into regions, decimates them with
quadric simplification, recenters and rescales them, and computes anchor points for nuclei
too small to have meshes (locus coeruleus, raphe, VTA, nucleus basalis, SCN). Under the
share-alike terms, `brain.bin` is distributed under the same CC BY-SA 2.1 JP license.
